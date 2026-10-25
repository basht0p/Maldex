using System.Buffers;
using System.Security.Cryptography;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Lifecycle;
using Maldex.Core.Media;
using Maldex.Data;
using Maldex.Infrastructure.Media;
using Maldex.Orchestrator.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Maldex.Orchestrator.Media;

/// <summary>The request breaks a rule (size, name, URL, chunk): 400.</summary>
public sealed class MediaRuleException(string message) : Exception(message);

/// <summary>The request is fine but cannot be done now (disk space, wrong state): 409.</summary>
public sealed class MediaRefusedException(string message) : Exception(message);

/// <summary>A chunk did not start where the server's copy ends: 409 with <c>receivedBytes</c>.</summary>
public sealed class MediaOffsetException(long receivedBytes) : Exception($"the chunk must start at byte {receivedBytes}")
{
    public long ReceivedBytes { get; } = receivedBytes;
}

/// <summary>No such media file: 404.</summary>
public sealed class MediaNotFoundException(Guid id) : Exception("media file not found")
{
    public Guid Id { get; } = id;
}

/// <summary>The file is used by an active template job: 409.</summary>
public sealed class MediaInUseException(string message) : Exception(message);

/// <summary>
/// The media library's write side: resumable chunked uploads, URL fetches, inspection, de-duplication, delete and the
/// abandoned-upload sweep. A row is the truth (what was received, what state); the staging file may be longer than the row
/// says after a crash and is cut back to it. Media content is never executed: it is hashed, then read by the inspector.
/// </summary>
public sealed class MediaIntake(
    MaldexDbContext db, MediaStore store, IMediaInspector inspector, SafeHttpFetcher fetcher,
    IOptions<MediaOptions> options, IOptions<TemplateJobOptions> jobs, TimeProvider time, MediaUploadState state)
{
    private const long MiB = 1024 * 1024;
    private const int MaxReasonLength = 500;
    private const int MaxFileNameLength = 255;
    private const string FallbackFetchName = "download.iso";

    private MediaOptions Opt => options.Value;

    public async Task<MediaFile> StartUploadAsync(string fileName, long size, MediaKind kind, string? sha256, Guid userId, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind)) throw new MediaRuleException("unknown media kind");
        if (size <= 0) throw new MediaRuleException("the file is empty");
        if (size > Opt.MaxBytes) throw new MediaRuleException($"the file is larger than the {Opt.MaxBytes / (double)(MiB * 1024):0.##} GiB limit");
        var name = CleanFileName(fileName) ?? throw new MediaRuleException("the file needs a name");
        var declared = NormaliseSha(sha256);

        // The check and the row are one step: a second start waits until the first one's bytes count as expected.
        await state.StartGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireDiskSpaceAsync(size, ct).ConfigureAwait(false);

            var now = time.GetUtcNow();
            var media = new MediaFile
            {
                Id = Guid.NewGuid(), Kind = kind, FileName = name, Size = size, Sha256 = declared, Source = MediaSource.Upload,
                Status = MediaStatus.Receiving, AddedByUserId = userId, AddedAt = now, LastActivityAt = now,
            };
            db.MediaFiles.Add(media);
            db.AuditLogs.Add(Audit(media.Id, userId, "media.upload.start", $"name={name}; size={size}; kind={kind}"));
            try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
            finally { db.ChangeTracker.Clear(); }
            return media;
        }
        finally
        {
            state.StartGate.Release();
        }
    }

    /// <summary>Refuses when free space is short of <paramref name="size"/> + the reserve + what other uploads still expect (a fetch passes 0: its size is unknown).</summary>
    private async Task RequireDiskSpaceAsync(long size, CancellationToken ct)
    {
        var expected = await db.MediaFiles.AsNoTracking()
            .Where(m => m.Status == MediaStatus.Receiving && m.Source == MediaSource.Upload)
            .SumAsync(m => (long?)(m.Size - m.ReceivedBytes), ct).ConfigureAwait(false) ?? 0;
        var requiredMiB = (size + MiB - 1) / MiB + jobs.Value.MinFreeGiB * 1024L + (expected + MiB - 1) / MiB;
        var freeMiB = store.FreeMiB();
        if (freeMiB < requiredMiB)
            throw new MediaRefusedException($"not enough disk space for this file: {freeMiB / 1024.0:0.#} GiB free, {requiredMiB / 1024.0:0.#} GiB required");
    }

    public async Task<(long ReceivedBytes, MediaStatus Status)> AppendChunkAsync(Guid id, long offset, Stream body, CancellationToken ct)
    {
        // No entry is made for an id that is not an upload receiving data.
        var first = await LoadAsync(id, ct).ConfigureAwait(false) ?? throw new MediaNotFoundException(id);
        if (first.Status != MediaStatus.Receiving || first.Source != MediaSource.Upload) throw new MediaRefusedException("this upload is no longer receiving data");
        var upload = state.Get(id);
        await upload.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var row = await LoadAsync(id, ct).ConfigureAwait(false) ?? throw new MediaNotFoundException(id);
            if (row.Status != MediaStatus.Receiving || row.Source != MediaSource.Upload)
                throw new MediaRefusedException("this upload is no longer receiving data");
            if (offset != row.ReceivedBytes) throw new MediaOffsetException(row.ReceivedBytes);

            var remaining = row.Size - row.ReceivedBytes;
            var limit = Math.Min(remaining, Opt.MaxChunkBytes);
            long written;
            try
            {
                written = await WriteChunkAsync(row, upload, body, limit, ct).ConfigureAwait(false);
                if (written == 0) throw new MediaRuleException("the chunk is empty");
            }
            catch
            {
                // Whatever reached the staging file is not recorded, and the running hash has taken it: both are rebuilt next time.
                upload.DropHash();
                throw;
            }
            if (written < 0)
            {
                upload.DropHash();
                throw new MediaRuleException(limit == remaining
                    ? "the chunk goes past the declared size of the file"
                    : $"the chunk is larger than the {Opt.MaxChunkBytes} byte limit");
            }

            row.ReceivedBytes += written;
            row.LastActivityAt = time.GetUtcNow();
            upload.HashedBytes = row.ReceivedBytes;
            try { await SaveAsync(row, null, ct).ConfigureAwait(false); }
            catch (DbUpdateConcurrencyException)
            {
                upload.DropHash();
                throw new MediaRefusedException("this upload is no longer receiving data");
            }
            if (row.ReceivedBytes == row.Size) state.Queue.Writer.TryWrite(new MediaWork(id, null));
            return (row.ReceivedBytes, row.Status);
        }
        finally
        {
            upload.Lock.Release();
        }
    }

    /// <summary>Appends the body to the staging file (cut back to what the row says) and the running hash. Returns the bytes written, or -1 when the body was longer than <paramref name="limit"/>.</summary>
    private async Task<long> WriteChunkAsync(MediaFile row, MediaUploadState.Upload upload, Stream body, long limit, CancellationToken ct)
    {
        if (upload.Hash is null || upload.HashedBytes != row.ReceivedBytes)
        {
            upload.DropHash();
            try
            {
                upload.Hash = row.ReceivedBytes == 0 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : await store.RehashStagingAsync(row.Id, row.ReceivedBytes, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                await FailAsync(row, "the staged data was lost", ct).ConfigureAwait(false);
                throw new MediaRefusedException("this upload is no longer receiving data");
            }
            upload.HashedBytes = row.ReceivedBytes;
        }

        var hash = upload.Hash!;
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            await using var file = store.OpenStagingForAppend(row.Id, row.ReceivedBytes);
            long written = 0;
            var over = false;
            while (true)
            {
                var want = (int)Math.Min(buffer.Length, limit + 1 - written);
                var read = await body.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read == 0) break;
                var take = (int)Math.Min(read, limit - written);
                if (take > 0)
                {
                    hash.AppendData(buffer, 0, take);
                    await file.WriteAsync(buffer.AsMemory(0, take), ct).ConfigureAwait(false);
                    written += take;
                }
                if (take < read)
                {
                    over = true;
                    break;
                }
            }
            await file.FlushAsync(ct).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
            return over ? -1 : written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<MediaFile> StartFetchAsync(Uri url, MediaKind kind, string? fileName, Guid userId, CancellationToken ct)
    {
        if (UrlFetchPolicy.RefuseUrl(url) is { } refused) throw new MediaRuleException(refused);
        if (!Enum.IsDefined(kind)) throw new MediaRuleException("unknown media kind");
        var given = fileName is null ? null : CleanFileName(fileName) ?? throw new MediaRuleException("the file needs a name");
        var shown = StripQuery(url);
        var name = given ?? CleanFileName(Uri.UnescapeDataString(Path.GetFileName(url.AbsolutePath))) ?? FallbackFetchName;

        MediaFile media;
        await state.StartGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RequireDiskSpaceAsync(0, ct).ConfigureAwait(false);
            var now = time.GetUtcNow();
            media = new MediaFile
            {
                Id = Guid.NewGuid(), Kind = kind, FileName = name, Source = MediaSource.Url, SourceUrl = shown,
                Status = MediaStatus.Receiving, AddedByUserId = userId, AddedAt = now, LastActivityAt = now,
            };
            db.MediaFiles.Add(media);
            db.AuditLogs.Add(Audit(media.Id, userId, "media.fetch.start", $"url={shown}; kind={kind}"));
            try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
            finally { db.ChangeTracker.Clear(); }
        }
        finally
        {
            state.StartGate.Release();
        }
        // The full URL (its query may carry a token) lives only in memory: a restart interrupts the fetch anyway.
        state.Queue.Writer.TryWrite(new MediaWork(media.Id, url, given is not null));
        return media;
    }

    /// <summary>Worker: hashes a fully received upload, de-duplicates and inspects it.</summary>
    public async Task CompleteAsync(Guid id, CancellationToken ct)
    {
        if (await LoadAsync(id, ct).ConfigureAwait(false) is not { Status: MediaStatus.Receiving, Source: MediaSource.Upload }) return;
        var upload = state.Get(id);
        await upload.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var row = await LoadAsync(id, ct).ConfigureAwait(false);
            if (row is not { Status: MediaStatus.Receiving, Source: MediaSource.Upload } || row.ReceivedBytes != row.Size) return;

            // Interrupted after the file was moved into place: its name is its (verified) hash, so pick up from there.
            if (row.Sha256 is { } known && !File.Exists(store.StagingPath(id)) && File.Exists(store.FinalPath(known)))
            {
                await FinishAsync(row, known, ct).ConfigureAwait(false);
                return;
            }

            string sha;
            if (upload.Hash is not null && upload.HashedBytes == row.Size) sha = Convert.ToHexStringLower(upload.Hash.GetCurrentHash());
            else
            {
                try
                {
                    using var hash = await store.RehashStagingAsync(id, row.Size, ct).ConfigureAwait(false);
                    sha = Convert.ToHexStringLower(hash.GetCurrentHash());
                }
                catch (Exception e) when (e is InvalidOperationException or IOException)
                {
                    await FailAsync(row, "the staged data was lost", ct).ConfigureAwait(false);
                    return;
                }
            }
            upload.DropHash();
            var declared = row.Sha256;
            row.Sha256 = null;
            if (declared is not null && declared != sha)
            {
                row.Sha256 = sha;
                await FailAsync(row, "the received file does not match the SHA-256 given at upload start", ct).ConfigureAwait(false);
                return;
            }
            await FinishAsync(row, sha, ct).ConfigureAwait(false);
        }
        finally
        {
            upload.Lock.Release();
            state.Remove(id); // whatever happened, nothing more arrives for a completed upload
        }
    }

    /// <summary>Worker: downloads <paramref name="url"/> into the staging file, then finishes as an upload does.</summary>
    public async Task RunFetchAsync(Guid id, Uri url, bool nameGiven, CancellationToken ct)
    {
        var row = await LoadAsync(id, ct).ConfigureAwait(false);
        if (row is not { Status: MediaStatus.Receiving, Source: MediaSource.Url }) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        state.RegisterFetch(id, cts);
        try
        {
            FetchResult result;
            long? stoppedAt = null;
            try
            {
                long reported = 0;
                await using var file = store.OpenStagingForAppend(id, 0);
                result = await fetcher.FetchAsync(url, file, async bytes =>
                {
                    if (stoppedAt is null && store.FreeMiB() < jobs.Value.MinFreeGiB * 1024L)
                    {
                        // Another download or upload used the space meanwhile: stop before the volume fills.
                        stoppedAt = bytes;
                        cts.Cancel();
                        return;
                    }
                    if (bytes - reported < 16 * MiB) return;
                    reported = bytes;
                    var now = time.GetUtcNow();
                    await db.MediaFiles.Where(m => m.Id == id && m.Status == MediaStatus.Receiving)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReceivedBytes, bytes).SetProperty(m => m.LastActivityAt, now), cts.Token).ConfigureAwait(false);
                }, cts.Token).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                DeleteStaging(id); // host shutdown: the row stays Receiving and the next start fails it as interrupted
                throw;
            }
            catch (Exception e) when (e is MediaFetchException or IOException or OperationCanceledException)
            {
                // A delete cancels the fetch: then the row is no longer Receiving and there is nothing to record.
                var current = await LoadAsync(id, CancellationToken.None).ConfigureAwait(false);
                var reason = stoppedAt is { } stopped ? $"not enough disk space: the fetch was stopped at {stopped / (double)(MiB * 1024):0.#} GiB"
                    : e is OperationCanceledException ? "the download was cancelled" : Clip(e.Message);
                if (current is { Status: MediaStatus.Receiving }) await FailAsync(current, reason, ct).ConfigureAwait(false);
                else DeleteStaging(id);
                return;
            }

            var fresh = await LoadAsync(id, ct).ConfigureAwait(false);
            if (fresh is not { Status: MediaStatus.Receiving })
            {
                DeleteStaging(id);
                return;
            }
            if (result.Bytes <= 0)
            {
                await FailAsync(fresh, "the download was empty", ct).ConfigureAwait(false);
                return;
            }
            fresh.Size = result.Bytes;
            fresh.ReceivedBytes = result.Bytes;
            fresh.LastActivityAt = time.GetUtcNow();
            if (!nameGiven && CleanFileName(result.SuggestedFileName) is { } suggested) fresh.FileName = suggested;
            try { await SaveAsync(fresh, null, ct).ConfigureAwait(false); }
            catch (DbUpdateConcurrencyException)
            {
                DeleteStaging(id); // deleted while finishing
                return;
            }
            await FinishAsync(fresh, result.Sha256, ct).ConfigureAwait(false);
        }
        finally
        {
            state.UnregisterFetch(id);
        }
    }

    /// <summary>Shared tail of an upload and a fetch: de-duplicate by hash, move into place, inspect, then Ready or Failed.</summary>
    private async Task FinishAsync(MediaFile row, string sha, CancellationToken ct)
    {
        row.Sha256 = sha;
        var existing = await db.MediaFiles.AsNoTracking()
            .Where(m => m.Id != row.Id && m.Sha256 == sha && m.Status == MediaStatus.Ready)
            .Select(m => m.FileName).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (existing is not null)
        {
            await FailAsync(row, $"already in the library as {existing}", ct).ConfigureAwait(false);
            return;
        }

        string path;
        try
        {
            // The hash is recorded first: if the process stops before the inspection ends, the next start finds the file by it.
            await SaveAsync(row, null, ct).ConfigureAwait(false);
            path = File.Exists(store.StagingPath(row.Id)) ? store.Finalize(row.Id, sha) : store.FinalPath(sha);
        }
        catch (DbUpdateConcurrencyException)
        {
            return; // deleted meanwhile
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await FailAsync(row, $"the file could not be stored: {Clip(e.Message)}", ct).ConfigureAwait(false);
            return;
        }

        MediaInspection inspection;
        try
        {
            inspection = await inspector.InspectAsync(path, row.Kind, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            inspection = new MediaInspection(null, $"the image could not be inspected: {Clip(e.Message)}");
        }
        if (inspection.Contents is null || inspection.Error is not null)
        {
            DeleteFinal(sha);
            await FailAsync(row, inspection.Error ?? "the image could not be inspected", ct).ConfigureAwait(false);
            return;
        }

        row.Contents = MediaContentsJson.Serialize(inspection.Contents);
        row.Status = MediaStatus.Ready;
        row.FailureReason = null;
        row.LastActivityAt = time.GetUtcNow();
        try
        {
            await SaveAsync(row, Audit(row.Id, row.AddedByUserId, "media.ready", $"sha256={sha}; size={row.Size}"), ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted while it was being inspected: the delete had no hash to find the file by.
            var current = await LoadAsync(row.Id, CancellationToken.None).ConfigureAwait(false);
            if (current is { Status: MediaStatus.Deleted } && !await HoldsFileAsync(sha, row.Id).ConfigureAwait(false)) DeleteFinal(sha);
        }
        catch (DbUpdateException)
        {
            // Another Ready file has this hash after all (the unique index): this one is a duplicate; the file on disk is theirs.
            row.Status = MediaStatus.Receiving;
            var winner = await db.MediaFiles.AsNoTracking().Where(m => m.Id != row.Id && m.Sha256 == sha && m.Status == MediaStatus.Ready).Select(m => m.FileName).FirstOrDefaultAsync(CancellationToken.None).ConfigureAwait(false);
            await FailAsync(row, $"already in the library as {winner ?? "another file"}", ct).ConfigureAwait(false);
        }
    }

    private Task<bool> HoldsFileAsync(string sha, Guid except) =>
        db.MediaFiles.AsNoTracking().AnyAsync(m => m.Id != except && m.Sha256 == sha && (m.Status == MediaStatus.Ready || m.Status == MediaStatus.Receiving));

    public async Task DeleteAsync(Guid id, Guid userId, CancellationToken ct)
    {
        await using (var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            var row = (await MediaLocks.LockAsync(db, [id], ct).ConfigureAwait(false)).SingleOrDefault() ?? throw new MediaNotFoundException(id);
            if (row.Status == MediaStatus.Deleted) throw new MediaRefusedException("the file is already deleted");

            var active = (await db.TemplateJobs.AsNoTracking()
                    .Where(j => TemplateJobStateMachine.ActiveStates.Contains(j.State) && j.MediaIds != "[]")
                    .Select(j => new { j.Id, j.TemplateId, j.MediaIds }).ToListAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(j => UsesMedia(j.MediaIds, id));
            if (active is not null)
            {
                var template = await db.Templates.AsNoTracking().Where(t => t.Id == active.TemplateId).Select(t => t.DisplayName).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                throw new MediaInUseException($"used by the build of {template ?? active.TemplateId} (job {active.Id})");
            }

            var wasReady = row.Status == MediaStatus.Ready;
            row.Status = MediaStatus.Deleted;
            row.LastActivityAt = time.GetUtcNow();
            db.AuditLogs.Add(Audit(id, userId, "media.delete", $"name={row.FileName}; sha256={row.Sha256}"));
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
            if (wasReady && row.Sha256 is not null) DeleteFinal(row.Sha256);
        }
        state.CancelFetch(id);
        DeleteStaging(id);
        state.Remove(id);
    }

    /// <summary>Fails uploads idle for longer than <see cref="MediaOptions.AbandonAfter"/> and deletes staging files nothing is receiving into.</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow() - Opt.AbandonAfter;
        var idle = await db.MediaFiles.AsNoTracking()
            .Where(m => m.Status == MediaStatus.Receiving && m.Source == MediaSource.Upload && m.ReceivedBytes < m.Size && m.LastActivityAt < cutoff)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in idle)
        {
            row.Status = MediaStatus.Failed;
            row.FailureReason = "upload abandoned (no data for 24 h)";
            row.LastActivityAt = time.GetUtcNow();
            try
            {
                await SaveAsync(row, Audit(row.Id, null, "media.abandon", $"name={row.FileName}; received={row.ReceivedBytes} of {row.Size}"), ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                continue; // a chunk arrived (or a delete) in the meantime
            }
            DeleteStaging(row.Id);
            state.Remove(row.Id);
        }

        // The sweep runs beside the worker's queue, so it lists files BEFORE reading rows: a row is always saved before its file
        // appears (the upload row before its staging file; Sha256 before Finalize), so any file listed here has its row visible below.
        var staging = store.StagingIds().ToList();
        var finals = Directory.Exists(Opt.Root) ? Directory.EnumerateFiles(Opt.Root, "*.iso").ToList() : [];

        var receiving = (await db.MediaFiles.AsNoTracking().Where(m => m.Status == MediaStatus.Receiving).Select(m => m.Id).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        foreach (var staged in staging)
            if (!receiving.Contains(staged)) DeleteStaging(staged);

        // Final files (<sha256>.iso) that no Ready file, and no upload on its way to Ready, holds are leftovers of a stop: delete them.
        var held = (await db.MediaFiles.AsNoTracking()
                .Where(m => m.Sha256 != null && (m.Status == MediaStatus.Ready || m.Status == MediaStatus.Receiving))
                .Select(m => m.Sha256!).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        foreach (var file in finals)
        {
            var sha = Path.GetFileNameWithoutExtension(file);
            if (sha.Length == 64 && sha.All(c => char.IsAsciiHexDigitLower(c)) && !held.Contains(sha)) DeleteFinal(sha);
        }
    }

    /// <summary>Once at start: fetches cannot resume, so they fail; uploads that had all their bytes are completed.</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        var receiving = await db.MediaFiles.AsNoTracking().Where(m => m.Status == MediaStatus.Receiving).ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in receiving)
        {
            if (row.Source == MediaSource.Url) await FailAsync(row, "interrupted", ct).ConfigureAwait(false);
            else if (row.Size > 0 && row.ReceivedBytes == row.Size) state.Queue.Writer.TryWrite(new MediaWork(row.Id, null));
        }
    }

    private async Task FailAsync(MediaFile row, string reason, CancellationToken ct)
    {
        row.Status = MediaStatus.Failed;
        row.FailureReason = Clip(reason);
        row.LastActivityAt = time.GetUtcNow();
        try
        {
            await SaveAsync(row, Audit(row.Id, null, "media.failed", $"reason={row.FailureReason}"), ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // deleted meanwhile: the delete's outcome stands
        }
        DeleteStaging(row.Id);
        state.Remove(row.Id);
    }

    private Task<MediaFile?> LoadAsync(Guid id, CancellationToken ct) =>
        db.MediaFiles.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct);

    /// <summary>Writes a row loaded without tracking (its row version is the concurrency check) and an optional audit entry in one save.</summary>
    private async Task SaveAsync(MediaFile row, AuditLog? audit, CancellationToken ct)
    {
        db.Entry(row).State = EntityState.Modified;
        if (audit is not null) db.AuditLogs.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
        finally { db.ChangeTracker.Clear(); }
    }

    private AuditLog Audit(Guid mediaId, Guid? userId, string action, string details) =>
        new() { At = time.GetUtcNow(), UserId = userId, Action = action, Target = mediaId.ToString(), Details = Clip(details, 1000) };

    /// <summary>Whether a job's media id list names the file. A list that cannot be read counts as naming it: deleting is the risky side.</summary>
    private static bool UsesMedia(string mediaIds, Guid id)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(mediaIds)?.Contains(id) ?? true;
        }
        catch (System.Text.Json.JsonException)
        {
            return true;
        }
    }

    // A file or directory that is not there is not an error: these run on clean-up paths.
    private void DeleteStaging(Guid id)
    {
        try { store.DeleteStaging(id); }
        catch (DirectoryNotFoundException) { }
    }

    private void DeleteFinal(string sha)
    {
        try { store.DeleteFinal(sha); }
        catch (DirectoryNotFoundException) { }
    }

    private static string Clip(string text, int max = MaxReasonLength) => text.Length <= max ? text : text[..max];

    private static string StripQuery(Uri url)
    {
        var builder = new UriBuilder(url) { Query = "", Fragment = "", UserName = "", Password = "" };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    private static string? NormaliseSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return null;
        var text = sha.Trim().ToLowerInvariant();
        if (text.Length != 64 || !text.All(Uri.IsHexDigit)) throw new MediaRuleException("the SHA-256 must be 64 hexadecimal characters");
        return text;
    }

    /// <summary>The last path segment, without control characters, trimmed and at most 255 characters; null when nothing is left.</summary>
    private static string? CleanFileName(string? name)
    {
        if (name is null) return null;
        var slash = name.LastIndexOfAny(['/', '\\']);
        var segment = new string((slash >= 0 ? name[(slash + 1)..] : name).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (segment.Length > MaxFileNameLength) segment = segment[..MaxFileNameLength].TrimEnd();
        return segment.Length == 0 || segment is "." or ".." ? null : segment;
    }
}

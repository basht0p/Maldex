using System.Security.Cryptography;
using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Text;
using Microsoft.Extensions.Logging;

namespace Maldex.Orchestrator;

/// <summary>A dropped file that passed every check and is now in the artifact store.</summary>
public sealed record DroppedFile(string GuestPath, StoredBlob Blob);

/// <summary>
/// Pulls files the guest created out of it. The guest is hostile: every file is capped (16 MiB), the whole collection is capped
/// (50 paths, 64 MiB, 60 s), and a file is only kept when its chunks are complete and in range and its SHA-256 matches.
/// A rejected or unreadable file is logged and skipped; it never fails the run.
/// </summary>
public sealed class DroppedFileCollector(IArtifactStore artifacts, TimeProvider time, ILogger logger)
{
    public const int MaxPaths = 50;
    public const long MaxTotalBytes = 64L * 1024 * 1024;
    public static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(20);

    /// <param name="inbox">Where the run's read loop delivers the agent's replies. Opened here, so nothing else may hold it.</param>
    /// <param name="excludeSha256">Files with this hash (the sample itself) are not stored again.</param>
    public async Task<IReadOnlyList<DroppedFile>> CollectAsync(
        IAgentChannel ch, TransferInbox inbox, IEnumerable<string> candidatePaths, string? excludeSha256, CancellationToken ct)
    {
        var paths = candidatePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxPaths)
            .ToList();
        var kept = new List<DroppedFile>();
        if (paths.Count == 0) return kept;

        using var overall = new CancellationTokenSource(OverallTimeout, time);
        using var overallLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, overall.Token);
        using var lease = inbox.Open();
        long received = 0;

        try
        {
            foreach (var path in paths)
            {
                if (received >= MaxTotalBytes)
                {
                    logger.LogWarning("Dropped-file collection stopped: the {Limit}-byte budget is used up.", MaxTotalBytes);
                    break;
                }

                var outcome = await CollectOneAsync(ch, lease.Reader, path, excludeSha256, overallLinked.Token).ConfigureAwait(false);
                received += outcome.BytesReceived;
                if (outcome.File is not null) kept.Add(outcome.File);
                if (outcome.Stop) break;
            }
        }
        catch (OperationCanceledException) when (overall.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Dropped-file collection stopped: the {Seconds} s limit was reached.", OverallTimeout.TotalSeconds);
        }

        return kept;
    }

    private sealed record Outcome(DroppedFile? File, long BytesReceived, bool Stop = false);

    private async Task<Outcome> CollectOneAsync(IAgentChannel ch, ChannelReader<AgentMessage> replies, string path, string? excludeSha256, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var shown = GuestText.Clean(path, 256);
        var transfer = new Transfer();

        using var timeout = new CancellationTokenSource(PerFileTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        CommandResultMessage? result = null;
        try
        {
            await ch.SendAsync(new CollectFileCommand(id, path), linked.Token).ConfigureAwait(false);
            while (result is null)
            {
                var message = await replies.ReadAsync(linked.Token).ConfigureAwait(false);
                switch (message)
                {
                    case FileChunkMessage chunk when chunk.TransferId == id:
                        transfer.Add(chunk);
                        break;
                    case CommandResultMessage r when r.CommandId == id:
                        result = r;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Dropped file {Path} skipped: the agent did not answer within {Seconds} s.", shown, PerFileTimeout.TotalSeconds);
            return new Outcome(null, transfer.BytesReceived);
        }
        catch (ChannelClosedException)
        {
            logger.LogWarning("Dropped-file collection stopped: the agent disconnected.");
            return new Outcome(null, transfer.BytesReceived, Stop: true);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Dropped-file collection stopped: the channel failed.");
            return new Outcome(null, transfer.BytesReceived, Stop: true);
        }

        if (!result.Success)
        {
            logger.LogInformation("Dropped file {Path} not collected: {Error}", shown, GuestText.Clean(result.Error, 200));
            return new Outcome(null, transfer.BytesReceived);
        }

        var problem = transfer.Verify(out var content);
        if (problem is not null)
        {
            logger.LogWarning("Dropped file {Path} rejected: {Problem}", shown, problem);
            return new Outcome(null, transfer.BytesReceived);
        }
        if (content!.Length == 0 || string.Equals(transfer.Sha256, excludeSha256, StringComparison.OrdinalIgnoreCase))
            return new Outcome(null, transfer.BytesReceived);

        try
        {
            await using var stream = new MemoryStream(content, writable: false);
            var blob = await artifacts.PutAsync("dropped", stream, AgentLimits.MaxDroppedFileBytes, ct).ConfigureAwait(false);
            return new Outcome(new DroppedFile(path, blob), transfer.BytesReceived);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Dropped file {Path} could not be stored.", shown);
            return new Outcome(null, transfer.BytesReceived);
        }
    }

    /// <summary>Reassembles one file from untrusted chunks. Anything wrong marks it rejected; it still counts the bytes it was sent.</summary>
    private sealed class Transfer
    {
        private byte[]?[]? _chunks;
        private int _count = -1;
        private long _total;
        private long _held;
        private string? _problem;

        public long BytesReceived { get; private set; }
        public string? Sha256 { get; private set; }

        public void Add(FileChunkMessage c)
        {
            byte[] data;
            try { data = Convert.FromBase64String(c.DataBase64); }
            catch (FormatException) { Reject("chunk is not valid base64"); return; }
            BytesReceived += data.Length;
            if (_problem is not null) return;

            if (c.TotalBytes is < 0 or > AgentLimits.MaxDroppedFileBytes) { Reject($"declared size {c.TotalBytes} exceeds the {AgentLimits.MaxDroppedFileBytes}-byte limit"); return; }
            if (c.Count is < 1 or > AgentLimits.MaxChunks) { Reject($"chunk count {c.Count} out of range"); return; }
            if (c.Index < 0 || c.Index >= c.Count) { Reject($"chunk index {c.Index} out of range"); return; }
            if (data.Length > AgentLimits.ChunkBytes) { Reject("chunk is larger than the chunk limit"); return; }

            if (_chunks is null)
            {
                _chunks = new byte[]?[c.Count];
                _count = c.Count;
                _total = c.TotalBytes;
                Sha256 = c.Sha256;
            }
            else if (c.Count != _count || c.TotalBytes != _total || !string.Equals(c.Sha256, Sha256, StringComparison.Ordinal))
            {
                Reject("chunks disagree about the file's size, count or hash");
                return;
            }

            if (_chunks[c.Index] is not null) { Reject($"chunk {c.Index} arrived twice"); return; }
            _held += data.Length;
            if (_held > _total) { Reject("the data is larger than the declared size"); return; }
            _chunks[c.Index] = data;
        }

        private void Reject(string why)
        {
            _problem ??= why;
            _chunks = null; // free what was buffered
            _held = 0;
        }

        /// <summary>Null when the file is complete and its hash matches; otherwise why not.</summary>
        public string? Verify(out byte[]? content)
        {
            content = null;
            if (_problem is not null) return _problem;
            if (_chunks is null) return "no data arrived";
            for (var i = 0; i < _chunks.Length; i++)
                if (_chunks[i] is null) return $"chunk {i} of {_count} is missing";
            if (_held != _total) return $"received {_held} bytes but {_total} were declared";

            var assembled = new byte[_total];
            var offset = 0;
            foreach (var part in _chunks)
            {
                part!.CopyTo(assembled, offset);
                offset += part.Length;
            }
            var actual = Convert.ToHexString(SHA256.HashData(assembled)).ToLowerInvariant();
            if (!string.Equals(actual, Sha256, StringComparison.Ordinal)) return "SHA-256 does not match the declared hash";
            content = assembled;
            return null;
        }
    }
}

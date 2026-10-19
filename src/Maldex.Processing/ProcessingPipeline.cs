using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Live;
using Maldex.Core.Text;
using Maldex.Core.Verdicts;
using Maldex.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Maldex.Processing;

/// <summary>
/// Turns a finished run into detections and a verdict: YARA over the sample, Suricata over the capture (when
/// there is one), heuristics over the stored telemetry. Safe to run again on the same task (a restart while
/// <c>Processing</c>): the first thing it does is discard that task's earlier detections and warnings.
/// A failing step becomes a processing warning; the task still completes.
/// </summary>
public sealed class ProcessingPipeline(
    IServiceScopeFactory scopes,
    IYaraScanner yara,
    ISuricataRunner suricata,
    IArtifactStore artifacts,
    ITaskEventPublisher publisher,
    TimeProvider time,
    ILogger<ProcessingPipeline> logger)
{
    private const int MaxSaveAttempts = 5;
    private const int RuleMax = 256;
    private const int TagsMax = 1024;
    private const int WarningMax = 2000;
    // GuestText.Clean appends a 12-character marker when it truncates.
    private const int SuffixLength = 12;

    public async Task ProcessAsync(Guid taskId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();

        var task = await db.Tasks.Include(t => t.Sample).SingleOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);
        if (task is null)
        {
            logger.LogWarning("Task {TaskId} does not exist; nothing to process.", taskId);
            return;
        }
        if (task.Status != RunStatus.Processing)
        {
            logger.LogWarning("Task {TaskId} is {Status}, not Processing; nothing to process.", taskId, task.Status);
            return;
        }

        await ResetAsync(db, task, ct).ConfigureAwait(false);

        var warnings = new List<string>();
        var results = new List<DetectionResult>();

        await RunStepAsync("yara", warnings, ct, async () => results.AddRange(await ScanSampleAsync(task, ct).ConfigureAwait(false))).ConfigureAwait(false);
        await RunStepAsync("suricata", warnings, ct, async () => results.AddRange(await RunSuricataAsync(db, taskId, ct).ConfigureAwait(false))).ConfigureAwait(false);
        await RunStepAsync("heuristics", warnings, ct, async () => results.AddRange(await EvaluateHeuristicsAsync(db, taskId, ct).ConfigureAwait(false))).ConfigureAwait(false);

        var verdict = VerdictEngine.Compute(results);
        db.Detections.AddRange(results.Select(r => ToEntity(taskId, r)));
        await CompleteAsync(db, task, verdict, warnings, ct).ConfigureAwait(false);

        try
        {
            await publisher.PublishStatusAsync(
                new TaskStatusUpdate(task.Id, task.Status, task.Verdict, task.DeadlineAt, "processing complete"), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The result is stored; clients re-sync from REST.
            logger.LogWarning(ex, "Publishing the final status of task {TaskId} failed.", taskId);
        }
    }

    /// <summary>Maps a detection to its row, fitting guest-derived text into the column sizes.</summary>
    public static Detection ToEntity(Guid taskId, DetectionResult r) => new()
    {
        TaskId = taskId,
        Source = r.Source,
        Rule = GuestText.Clean(r.Rule, RuleMax - SuffixLength),
        Severity = r.Severity,
        Tags = JoinTags(r.Tags),
        Target = GuestText.Clean(r.Target),
        Details = r.Details is null ? null : GuestText.Clean(r.Details),
    };

    /// <summary>Comma-joins the tags, dropping whole tags from the end so the result never exceeds the 1024-char column.</summary>
    private static string JoinTags(IReadOnlyList<string> tags)
    {
        var kept = new List<string>();
        var length = 0;
        foreach (var raw in tags)
        {
            var tag = GuestText.Clean(raw, 64).Replace(",", "");
            if (tag.Length == 0) continue;
            var next = length + tag.Length + (kept.Count > 0 ? 1 : 0);
            if (next > TagsMax) break;
            kept.Add(tag);
            length = next;
        }
        return string.Join(',', kept);
    }

    /// <summary>One transaction: drop the task's earlier detections and clear its warnings (this is what makes a rerun idempotent).</summary>
    private static async Task ResetAsync(MaldexDbContext db, AnalysisTask task, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await db.Detections.Where(d => d.TaskId == task.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            task.ProcessingWarnings = [];
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                await db.Entry(task).ReloadAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RunStepAsync(string step, List<string> warnings, CancellationToken ct, Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Processing step {Step} failed.", step);
            warnings.Add(GuestText.Clean($"{step} failed: {ex.Message}", WarningMax));
        }
    }

    private async Task<IReadOnlyList<DetectionResult>> ScanSampleAsync(AnalysisTask task, CancellationToken ct)
    {
        if (task.Sample is null) return []; // URL task: nothing to scan
        var target = string.IsNullOrEmpty(task.OriginalFileName) ? task.Sample.Sha256 : task.OriginalFileName;
        using var copy = await TempCopy.CreateAsync(artifacts, task.Sample.BlobKey, ct).ConfigureAwait(false);
        return await yara.ScanAsync(copy.Path, target, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DetectionResult>> RunSuricataAsync(MaldexDbContext db, Guid taskId, CancellationToken ct)
    {
        var pcaps = await db.Artifacts.AsNoTracking()
            .Where(a => a.TaskId == taskId && a.Kind == ArtifactKind.Pcap)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var found = new List<DetectionResult>();
        foreach (var pcap in pcaps)
        {
            using var copy = await TempCopy.CreateAsync(artifacts, pcap.BlobKey, ct).ConfigureAwait(false);
            found.AddRange(await suricata.RunAsync(copy.Path, ct).ConfigureAwait(false));
        }
        return found;
    }

    private static async Task<IReadOnlyList<DetectionResult>> EvaluateHeuristicsAsync(MaldexDbContext db, Guid taskId, CancellationToken ct)
    {
        var processes = await db.ProcessNodes.AsNoTracking().Where(p => p.TaskId == taskId).OrderBy(p => p.Id).ToListAsync(ct).ConfigureAwait(false);
        var events = await db.BehaviorEvents.AsNoTracking().Where(e => e.TaskId == taskId).OrderBy(e => e.Id).ToListAsync(ct).ConfigureAwait(false);
        return BehaviorHeuristics.Evaluate(processes, events);
    }

    /// <summary>
    /// Stores detections, warnings and verdict and moves the task to <c>Completed</c> in one save. The task row is also
    /// written by the API, so it is reloaded first and the save retried on a concurrency conflict.
    /// </summary>
    private async Task CompleteAsync(MaldexDbContext db, AnalysisTask task, Verdict verdict, List<string> warnings, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await db.Entry(task).ReloadAsync(ct).ConfigureAwait(false);
            task.ProcessingWarnings = warnings;
            task.Verdict = verdict;
            task.TransitionTo(RunStatus.Completed, "processing complete", time.GetUtcNow());
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
            {
                foreach (var h in task.StatusHistory.Where(h => db.Entry(h).State == EntityState.Added).ToList())
                {
                    db.Entry(h).State = EntityState.Detached;
                    task.StatusHistory.Remove(h);
                }
            }
        }
    }

    /// <summary>A read-only (0400) temporary copy of a stored blob, because the scanners need a filesystem path. Deleted on dispose.</summary>
    private sealed class TempCopy : IDisposable
    {
        public string Path { get; }

        private TempCopy(string path) => Path = path;

        public static async Task<TempCopy> CreateAsync(IArtifactStore store, string key, CancellationToken ct)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maldex-scan-" + Guid.NewGuid().ToString("N"));
            var copy = new TempCopy(path);
            try
            {
                await using var source = await store.OpenReadAsync(key, ct).ConfigureAwait(false);
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead; // 0400
                await using var target = new FileStream(path, options);
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
                return copy;
            }
            catch
            {
                copy.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Live;
using Maldex.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maldex.Processing;

/// <summary>
/// Every <see cref="PollInterval"/>, processes tasks in <c>Processing</c>, oldest first and strictly one at a time.
/// A task left in <c>Processing</c> by a restart is simply picked up again (the pipeline is idempotent).
/// A task whose processing keeps throwing is retried with backoff and, after <c>Maldex:Processing:MaxAttempts</c>
/// attempts (default 5), moved to <c>Failed</c>.
/// </summary>
public sealed class ProcessingWorker(
    IServiceScopeFactory scopes,
    IProcessingPipeline pipeline,
    TimeProvider time,
    IOptions<ProcessingOptions> options,
    ILogger<ProcessingWorker> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private const int DefaultMaxAttempts = 5;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    // In memory only: a restart gives every task a fresh set of attempts.
    private readonly Dictionary<Guid, (int Failures, DateTimeOffset RetryAt)> _failures = [];

    private int MaxAttempts => options.Value.MaxAttempts > 0 ? options.Value.MaxAttempts : DefaultMaxAttempts;

    /// <summary>Delay before the next attempt after <paramref name="failures"/> failures: 2 s, 4 s, 8 s ... capped at 60 s.</summary>
    public static TimeSpan BackoffFor(int failures)
    {
        var seconds = Math.Pow(2, Math.Min(Math.Max(failures, 1), 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval, time);
            do
            {
                try
                {
                    await ProcessPendingAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Processing pass failed; will retry.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host shutdown
        }
    }

    /// <summary>Processes every task currently in <c>Processing</c>, oldest first. Returns the ids it processed.</summary>
    public async Task<IReadOnlyList<Guid>> ProcessPendingAsync(CancellationToken ct)
    {
        var done = new List<Guid>();
        await PruneFailuresAsync(ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        HashSet<Guid> tried = [.. _failures.Where(f => f.Value.RetryAt > now).Select(f => f.Key)]; // still backing off
        while (!ct.IsCancellationRequested)
        {
            var next = await OldestProcessingAsync(tried, ct).ConfigureAwait(false);
            if (next is null) break;
            tried.Add(next.Value);
            try
            {
                await pipeline.ProcessAsync(next.Value, ct).ConfigureAwait(false);
                _failures.Remove(next.Value);
                done.Add(next.Value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Don't let one bad task starve the others: back off and retry, until the attempt cap fails the task.
                await RecordFailureAsync(next.Value, ex, ct).ConfigureAwait(false);
            }
        }
        return done;
    }

    private async Task RecordFailureAsync(Guid taskId, Exception ex, CancellationToken ct)
    {
        var failures = (_failures.TryGetValue(taskId, out var f) ? f.Failures : 0) + 1;
        if (failures < MaxAttempts)
        {
            var delay = BackoffFor(failures);
            _failures[taskId] = (failures, time.GetUtcNow() + delay);
            logger.LogError(ex, "Processing task {TaskId} failed (attempt {Attempt} of {Max}); retrying in {Delay}.", taskId, failures, MaxAttempts, delay);
            return;
        }

        logger.LogError(ex, "Processing task {TaskId} failed {Attempts} times; giving up.", taskId, failures);
        try
        {
            await FailTaskAsync(taskId, $"processing failed: {ex.Message}", ct).ConfigureAwait(false);
            _failures.Remove(taskId);
        }
        catch (Exception inner) when (inner is not OperationCanceledException)
        {
            // Could not record the failure; keep the entry so a later pass tries again.
            _failures[taskId] = (failures, time.GetUtcNow() + MaxBackoff);
            logger.LogError(inner, "Could not mark task {TaskId} as failed; will retry.", taskId);
        }
    }

    /// <summary>Forgets tasks that are no longer in Processing (completed, failed elsewhere, deleted).</summary>
    private async Task PruneFailuresAsync(CancellationToken ct)
    {
        if (_failures.Count == 0) return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var ids = _failures.Keys.ToList();
        var still = await db.Tasks.AsNoTracking()
            .Where(t => ids.Contains(t.Id) && t.Status == RunStatus.Processing)
            .Select(t => t.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var id in ids.Except(still)) _failures.Remove(id);
    }

    private async Task FailTaskAsync(Guid taskId, string reason, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var task = await db.Tasks.SingleOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);
        if (task is null) return;

        var at = time.GetUtcNow();
        var moved = await TaskTransitions.TransitionWithRetryAsync(db, task, RunStatus.Failed, reason, at, t =>
        {
            t.FailureReason = reason;
            t.EndedAt ??= at;
        }, ct, onlyFrom: RunStatus.Processing).ConfigureAwait(false);
        if (!moved) return;

        try
        {
            var publisher = scope.ServiceProvider.GetRequiredService<ITaskEventPublisher>();
            await publisher.PublishStatusAsync(new TaskStatusUpdate(task.Id, task.Status, task.Verdict, task.DeadlineAt, reason), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Publishing the failure of task {TaskId} failed.", taskId);
        }
    }

    private async Task<Guid?> OldestProcessingAsync(HashSet<Guid> skip, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var ids = await db.Tasks.AsNoTracking()
            .Where(t => t.Status == RunStatus.Processing)
            .OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => t.Id)
            .Take(skip.Count + 1)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var id in ids) if (!skip.Contains(id)) return id;
        return null;
    }
}

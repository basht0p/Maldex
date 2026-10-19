using Maldex.Core.Domain;
using Maldex.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maldex.Processing;

/// <summary>
/// Every <see cref="PollInterval"/>, processes tasks in <c>Processing</c>, oldest first and strictly one at a time.
/// A task left in <c>Processing</c> by a restart is simply picked up again (the pipeline is idempotent).
/// </summary>
public sealed class ProcessingWorker(
    IServiceScopeFactory scopes,
    ProcessingPipeline pipeline,
    TimeProvider time,
    ILogger<ProcessingWorker> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

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
        HashSet<Guid> tried = [];
        while (!ct.IsCancellationRequested)
        {
            var next = await OldestProcessingAsync(tried, ct).ConfigureAwait(false);
            if (next is null) break;
            tried.Add(next.Value);
            try
            {
                await pipeline.ProcessAsync(next.Value, ct).ConfigureAwait(false);
                done.Add(next.Value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Don't let one bad task starve the others; it stays in Processing and is retried on the next pass.
                logger.LogError(ex, "Processing task {TaskId} failed; will retry.", next.Value);
            }
        }
        return done;
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

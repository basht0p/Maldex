using Maldex.Core.Health;
using Maldex.Data.Templates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maldex.Processing;

/// <summary>
/// Runs <see cref="RetentionService"/> five minutes after startup, then every 24 hours; each cycle also sweeps the disks of
/// retired template versions (<see cref="TemplateCatalog.SweepAsync"/>).
/// </summary>
public sealed class RetentionWorker(RetentionService retention, BlobGarbageCollector blobGc, IServiceScopeFactory scopes, TimeProvider time, ILogger<RetentionWorker> logger, WorkerHealth? health = null) : BackgroundService
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    // The startup delay is not a stall: the first tick is due after StartupDelay + Interval at the latest.
    private readonly WorkerHealth? _health = WorkerHealth.RegisterOn(health, "retention", StartupDelay + Interval);

    /// <summary>
    /// Deletes the disks of Retired template versions no active task uses; the catalog writes each deletion's system
    /// audit row in the same save that marks the version Deleted.
    /// </summary>
    public async Task SweepTemplatesAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TemplateCatalog>().SweepAsync(ct).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, time, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                _health?.Tick("retention");
                try
                {
                    await retention.PurgeAsync(time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Retention purge failed; will retry at the next interval.");
                }
                try
                {
                    await blobGc.SweepAsync(time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Blob GC sweep failed; will retry at the next interval.");
                }
                try
                {
                    await SweepTemplatesAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Template version sweep failed; will retry at the next interval.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host shutdown
        }
    }
}

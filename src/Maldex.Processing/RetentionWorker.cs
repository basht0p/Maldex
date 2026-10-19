using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maldex.Processing;

/// <summary>Runs <see cref="RetentionService"/> five minutes after startup, then every 24 hours.</summary>
public sealed class RetentionWorker(RetentionService retention, TimeProvider time, ILogger<RetentionWorker> logger) : BackgroundService
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, time, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                try
                {
                    await retention.PurgeAsync(time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Retention purge failed; will retry at the next interval.");
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

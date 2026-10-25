using Maldex.Core.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maldex.Infrastructure.Media;

/// <summary>
/// Cleans up loop mounts a crashed or timed-out inspection left under the media root: once at startup, then every <see cref="SweepInterval"/>.
/// Runs in the background (an unmount can take tens of seconds and must not hold up the host's start). Only Real mode has a
/// <see cref="LoopMountMediaInspector"/>; without one each pass is a no-op, so the loop still reports its liveness.
/// </summary>
public sealed class MediaMountSweepService(IServiceProvider services, TimeProvider time, ILogger<MediaMountSweepService> log, WorkerHealth? health = null) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly WorkerHealth? _health = WorkerHealth.RegisterOn(health, "media-mount-sweep", SweepInterval);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(SweepInterval, time);
            do
            {
                _health?.Tick("media-mount-sweep");
                // Not behind the real-mode start gate (unlike MediaWorker): safe because the in-flight guard skips live inspections, and Maldex is a single host.
                if (services.GetService<LoopMountMediaInspector>() is { } inspector)
                {
                    try { await inspector.SweepAsync(stoppingToken).ConfigureAwait(false); }
                    catch (Exception e) when (!stoppingToken.IsCancellationRequested) { log.LogWarning(e, "Media mount sweep failed"); }
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

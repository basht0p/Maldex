using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maldex.Infrastructure.Media;

/// <summary>
/// Cleans up loop mounts a crashed or timed-out inspection left under the media root, once at startup. Runs in the background:
/// an unmount can take tens of seconds and must not hold up the host's start.
/// </summary>
public sealed class MediaMountSweepService(LoopMountMediaInspector inspector, ILogger<MediaMountSweepService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await inspector.SweepAsync(stoppingToken).ConfigureAwait(false); }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested) { log.LogWarning(e, "Media mount sweep failed"); }
    }
}

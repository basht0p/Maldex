using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maldex.Infrastructure.Media;

/// <summary>Cleans up loop mounts a crashed or timed-out inspection left under the media root, once at startup.</summary>
public sealed class MediaMountSweepService(LoopMountMediaInspector inspector, ILogger<MediaMountSweepService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try { await inspector.SweepAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Media mount sweep failed"); }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

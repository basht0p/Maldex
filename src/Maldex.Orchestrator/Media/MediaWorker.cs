using Maldex.Infrastructure.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maldex.Orchestrator.Media;

/// <summary>
/// Runs the media library's background work: recovers at start, then completes uploads and runs URL fetches one at a time
/// from <see cref="MediaUploadState.Queue"/>, and sweeps abandoned uploads (and, in Real mode, stale loop mounts) hourly.
/// </summary>
public sealed class MediaWorker(IServiceScopeFactory scopes, MediaUploadState state, IServiceProvider services, TimeProvider time, ILogger<MediaWorker> logger) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunSafelyAsync("recover", (intake, ct) => intake.RecoverAsync(ct), stoppingToken).ConfigureAwait(false);
            var sweeping = SweepLoopAsync(stoppingToken);
            await foreach (var work in state.Queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunSafelyAsync(work.FetchUrl is null ? "complete" : "fetch",
                    (intake, ct) => work.FetchUrl is { } url ? intake.RunFetchAsync(work.Id, url, work.NameGiven, ct) : intake.CompleteAsync(work.Id, ct),
                    stoppingToken).ConfigureAwait(false);
            }
            await sweeping.ConfigureAwait(false);
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // host shutdown
        }
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SweepInterval, time);
        do
        {
            await RunSafelyAsync("sweep", (intake, token) => intake.SweepAsync(token), ct).ConfigureAwait(false);
            // The startup sweep of stale mounts is a BackgroundService of its own; this is the hourly one (Real mode only).
            if (services.GetService<LoopMountMediaInspector>() is { } mounts)
            {
                try { await mounts.SweepAsync(ct).ConfigureAwait(false); }
                catch (Exception e) when (!ct.IsCancellationRequested) { logger.LogWarning(e, "Media mount sweep failed"); }
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task RunSafelyAsync(string what, Func<MediaIntake, CancellationToken, Task> step, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await step(scope.ServiceProvider.GetRequiredService<MediaIntake>(), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError(e, "Media {Step} failed", what);
        }
    }
}

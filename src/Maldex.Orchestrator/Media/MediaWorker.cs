using System.Threading.Channels;
using Maldex.Infrastructure.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maldex.Orchestrator.Media;

/// <summary>
/// Runs the media library's background work: recovers at start, then completes uploads from <see cref="MediaUploadState.Queue"/> one at a time and runs URL fetches
/// one at a time on a consumer of their own, and sweeps abandoned uploads (and, in Real mode, stale loop mounts) hourly.
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
            // A fetch can run for hours: it has its own consumer (one at a time) so upload completions keep flowing.
            var fetches = Channel.CreateUnbounded<MediaWork>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var fetching = FetchLoopAsync(fetches.Reader, stoppingToken);
            try
            {
                await foreach (var work in state.Queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                {
                    if (work.FetchUrl is not null) fetches.Writer.TryWrite(work);
                    else await RunSafelyAsync("complete", (intake, ct) => intake.CompleteAsync(work.Id, ct), stoppingToken).ConfigureAwait(false);
                }
            }
            finally
            {
                fetches.Writer.TryComplete();
            }
            await fetching.ConfigureAwait(false);
            await sweeping.ConfigureAwait(false);
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // host shutdown
        }
    }

    private async Task FetchLoopAsync(ChannelReader<MediaWork> reader, CancellationToken ct)
    {
        await foreach (var work in reader.ReadAllAsync(ct).ConfigureAwait(false))
            await RunSafelyAsync("fetch", (intake, token) => intake.RunFetchAsync(work.Id, work.FetchUrl!, work.NameGiven, token), ct).ConfigureAwait(false);
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

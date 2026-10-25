using System.Collections.Concurrent;
using Maldex.Core.Domain;
using Maldex.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maldex.Orchestrator.Templates;

/// <summary>
/// Once the orchestrator's startup reconcile is done, starts queued template jobs (oldest first) while VM slots are free, one
/// <see cref="TemplateCoordinator"/> run per job. Slots are the shared <see cref="VmSlots"/> count; runs this worker has started
/// that have not taken their slot yet are counted too, so a burst of jobs does not copy disks it cannot boot. The slot itself
/// is taken by the coordinator under the <see cref="VmSlotGate"/>. A job with a cancel request is started regardless, so that
/// the coordinator ends it at once.
/// </summary>
public sealed class TemplateJobWorker(
    IServiceScopeFactory scopes,
    Reconciler reconciler,
    TemplateCoordinator coordinator,
    IOptions<OrchestratorOptions> options,
    TimeProvider time,
    ILogger<TemplateJobWorker> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private const int CandidateWindow = 50;

    private readonly CancellationTokenSource _runsCts = new();
    private readonly ConcurrentDictionary<Guid, Task> _runs = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The reconcile destroys every VM it finds and fails interrupted jobs: never start one before it has finished.
            await reconciler.Reconciled.WaitAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(PollInterval, time);
            do
            {
                try
                {
                    await ClaimAndStartAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Starting queued template jobs failed; will retry.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // host shutdown
        }
        finally
        {
            await StopRunsAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One scheduling pass: starts the queued jobs that fit. Returns the ids it started.</summary>
    public async Task<IReadOnlyList<Guid>> ClaimAndStartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();

        var queued = await db.TemplateJobs.AsNoTracking()
            .Where(j => j.State == TemplateJobState.Queued)
            .OrderBy(j => j.CreatedAt).ThenBy(j => j.Id)
            .Take(CandidateWindow)
            .Select(j => new { j.Id, j.CancelRequestedAt })
            .ToListAsync(ct).ConfigureAwait(false);
        if (queued.Count == 0) return [];

        // Busy slots (the one VmSlots definition), plus the runs started here that have not taken their slot yet.
        var busy = await VmSlots.BusyAsync(db, ct).ConfigureAwait(false);
        var mine = _runs.Keys.ToArray();
        var notYetHolding = mine.Length == 0 ? 0 : await db.TemplateJobs.AsNoTracking()
            .CountAsync(j => mine.Contains(j.Id) && j.SlotHeldAt == null && (j.State == TemplateJobState.Queued || j.State == TemplateJobState.Preparing), ct).ConfigureAwait(false);
        var free = options.Value.MaxConcurrentVms - busy - notYetHolding;

        var started = new List<Guid>();
        foreach (var job in queued)
        {
            if (_runs.ContainsKey(job.Id)) continue;
            if (job.CancelRequestedAt is null)
            {
                if (free <= 0) continue;
                free--;
            }
            Start(job.Id);
            started.Add(job.Id);
        }
        return started;
    }

    private void Start(Guid id)
    {
        var token = _runsCts.Token;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Task.Run(async () =>
        {
            await gate.Task.ConfigureAwait(false); // registered before it can finish and remove itself
            try
            {
                await coordinator.RunAsync(id, token).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Template job {JobId} ended with an unhandled error.", id);
            }
            finally
            {
                _runs.TryRemove(id, out _);
            }
        }, CancellationToken.None);
        _runs[id] = run;
        gate.SetResult();
    }

    /// <summary>Cancels every job run this worker started and waits for them to unwind (VMs, leases and files are released on the way out).</summary>
    public async Task StopRunsAsync()
    {
        await _runsCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_runs.Values).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _runsCts.Dispose();
        base.Dispose();
    }
}

using System.Collections.Concurrent;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Health;
using Maldex.Core.Scheduling;
using Maldex.Data;
using Maldex.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maldex.Orchestrator;

/// <summary>
/// Reconciles on start, then once a second claims queued tasks (interactive first, then FIFO) up to the free VM
/// slots and runs each one on its own background task. Slots are counted from the database, so a second
/// claimer (or a restart) can never push the host past <see cref="OrchestratorOptions.MaxConcurrentVms"/>.
/// </summary>
public sealed class OrchestratorWorker(
    IServiceScopeFactory scopes,
    Reconciler reconciler,
    RunCoordinator coordinator,
    VmSlotGate slotGate,
    IDiskSpaceProbe disk,
    IOptions<OrchestratorOptions> options,
    IOptions<ArtifactStoreOptions> artifactOptions,
    TimeProvider time,
    ILogger<OrchestratorWorker> logger,
    WorkerHealth? health = null) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private const int CandidateWindow = 100;

    private readonly OrchestratorOptions _options = options.Value;
    private readonly CancellationTokenSource _runsCts = new();
    private readonly ConcurrentDictionary<Guid, Task> _runs = new();

    // Registered at construction: a worker the start gate never lets run shows up as stalled.
    private readonly WorkerHealth? _health = WorkerHealth.RegisterOn(health, "orchestrator", PollInterval);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _health?.Tick("orchestrator");
            await ReconcileWithRetryAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(PollInterval, time);
            do
            {
                _health?.Tick("orchestrator");
                try
                {
                    await ClaimAndStartAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Claiming queued tasks failed; will retry.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host shutdown
        }
        finally
        {
            // Runs left mid-flight are failed by the reconciler on the next start.
            await StopRunsAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Bounded retry of the startup reconcile. Never claims before a reconcile succeeded (leftovers would hold slots and
    /// networks); after the last attempt the exception escapes, faulting ExecuteTask, which the host wrapper turns into a stop.
    /// </summary>
    private async Task ReconcileWithRetryAsync(CancellationToken ct)
    {
        var attempts = Math.Max(1, _options.ReconcileAttempts);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await reconciler.ReconcileAsync(ct).ConfigureAwait(false);
                if (attempt > 1) logger.LogInformation("Startup reconcile succeeded on attempt {Attempt}.", attempt);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < attempts)
            {
                logger.LogWarning(ex, "Startup reconcile failed (attempt {Attempt}/{Attempts}); retrying in {Delay}.", attempt, attempts, _options.ReconcileRetryDelay);
                await Task.Delay(_options.ReconcileRetryDelay, time, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogCritical(ex, "Startup reconcile failed {Attempts} times; the orchestrator worker stops.", attempts);
                throw;
            }
        }
    }

    /// <summary>
    /// One scheduling pass: claims queued tasks into the free slots and starts a run for each.
    /// Returns the ids it claimed.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ClaimAndStartAsync(CancellationToken ct)
    {
        var free = disk.FreeMiB(artifactOptions.Value.Root);
        if (free < _options.MinFreeDiskMiB)
        {
            logger.LogWarning("Only {FreeMiB} MiB free under the artifact root (minimum {MinMiB}); not starting new runs.", free, _options.MinFreeDiskMiB);
            return [];
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();

        // Count and claim under the slot gate: a template job taking a slot does the same, so both never take the same one.
        using var gate = await slotGate.EnterAsync(ct).ConfigureAwait(false);
        var busy = await VmSlots.BusyAsync(db, ct).ConfigureAwait(false);
        var slots = _options.MaxConcurrentVms - busy;
        if (slots <= 0) return [];

        var queued = await db.Tasks.AsNoTracking()
            .Where(t => t.Status == RunStatus.Queued)
            .OrderByDescending(t => t.Interactive).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .Take(CandidateWindow)
            .Select(t => new QueuedTaskInfo(t.Id, t.Interactive, t.CreatedAt))
            .ToListAsync(ct).ConfigureAwait(false);

        var claimed = new List<Guid>();
        foreach (var id in TaskPrioritizer.SelectNext(queued, slots))
        {
            if (!await TryClaimAsync(id, ct).ConfigureAwait(false)) continue;
            claimed.Add(id);
            Start(id);
        }

        return claimed;
    }

    /// <summary>Queued → Preparing. Returns false if another claimer got there first.</summary>
    private async Task<bool> TryClaimAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        try
        {
            var task = await db.Tasks.SingleOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
            if (task is null || task.Status != RunStatus.Queued) return false;

            task.TransitionTo(RunStatus.Preparing, "picked up", time.GetUtcNow());
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Task {TaskId} was claimed (or cancelled) by someone else; skipping.", id);
            return false;
        }
    }

    private void Start(Guid id)
    {
        var token = _runsCts.Token;
        var run = Task.Run(async () =>
        {
            try
            {
                await coordinator.RunAsync(id, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Run {TaskId} ended with an unhandled error.", id);
            }
            finally
            {
                _runs.TryRemove(id, out _);
            }
        }, CancellationToken.None);
        _runs[id] = run;
    }

    /// <summary>Cancels every run this worker started and waits for them to unwind (VMs and leases are released on the way out).</summary>
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

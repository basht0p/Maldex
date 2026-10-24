using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Orchestrator.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Maldex.Orchestrator;

/// <summary>
/// Runs once when the orchestrator starts. Nothing survives a restart of the control plane, so every VM and
/// network lease still around is a leftover and every task that was mid-run is failed. <c>Processing</c> tasks
/// only need the database, so they are left for the processing worker to resume. Template jobs that had started are
/// failed as interrupted and their working files deleted; queued ones simply run later.
/// </summary>
public sealed class Reconciler(
    IServiceScopeFactory scopes,
    IHypervisor hypervisor,
    IEgressRouter egress,
    ITemplateStore templates,
    TimeProvider time,
    ILogger<Reconciler> logger)
{
    public const string RestartReason = "orchestrator restart";

    private const int MaxSaveAttempts = 3;

    private static readonly RunStatus[] InFlight = [RunStatus.Preparing, RunStatus.Running, RunStatus.Collecting];

    private static readonly TemplateJobState[] JobsInFlight =
        [TemplateJobState.Preparing, TemplateJobState.Configuring, TemplateJobState.Sealing, TemplateJobState.Verifying];

    private readonly TaskCompletionSource _reconciled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when a reconcile has succeeded. Anything that starts VMs waits for it: the reconcile destroys every VM it finds.
    /// </summary>
    public Task Reconciled => _reconciled.Task;

    public async Task ReconcileAsync(CancellationToken ct)
    {
        // Guests first, then their networks, then the bookkeeping.
        foreach (var vm in await hypervisor.ListAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await hypervisor.DestroyAsync(vm, ct).ConfigureAwait(false);
                logger.LogWarning("Destroyed leftover VM {Vm} (task {TaskId}).", vm.Name, vm.TaskId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Destroying leftover VM {Vm} failed.", vm.Name);
            }
        }

        foreach (var lease in await egress.ListAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await egress.ReleaseAsync(lease, ct).ConfigureAwait(false);
                logger.LogWarning("Released leftover network lease {Network} (task {TaskId}).", lease.NetworkId, lease.TaskId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Releasing leftover network lease {Network} failed.", lease.NetworkId);
            }
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var ids = await db.Tasks.AsNoTracking()
            .Where(t => InFlight.Contains(t.Status))
            .Select(t => t.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var id in ids)
            await FailAsync(db, id, ct).ConfigureAwait(false);

        if (ids.Count > 0) logger.LogWarning("Failed {Count} in-flight task(s) after an orchestrator restart.", ids.Count);

        var jobIds = await db.TemplateJobs.AsNoTracking()
            .Where(j => JobsInFlight.Contains(j.State))
            .Select(j => j.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var id in jobIds)
            await FailJobAsync(db, id, ct).ConfigureAwait(false);

        if (jobIds.Count > 0) logger.LogWarning("Failed {Count} interrupted template job(s) after an orchestrator restart.", jobIds.Count);

        _reconciled.TrySetResult();
    }

    /// <summary>Deletes the job's files (working copy or sealed, never recorded), then marks it Failed with its system audit row.</summary>
    private async Task FailJobAsync(MaldexDbContext db, Guid id, CancellationToken ct)
    {
        var stem = TemplateCoordinator.StemOf(id);
        try
        {
            await templates.DeleteAsync(stem, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Deleting the files of interrupted template job {JobId} ({Stem}) failed.", id, stem);
        }

        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var job = await db.TemplateJobs.SingleAsync(j => j.Id == id, ct).ConfigureAwait(false);
            if (!JobsInFlight.Contains(job.State)) return;

            var now = time.GetUtcNow();
            job.TransitionTo(TemplateJobState.Failed, TemplateCoordinator.InterruptedReason, now);
            job.FailureReason = TemplateCoordinator.InterruptedReason;
            db.AuditLogs.Add(new AuditLog
            {
                At = now, UserId = null, Action = "template.job.fail", Target = job.TemplateId,
                Details = $"job={job.Id}; reason={TemplateCoordinator.InterruptedReason}",
            });
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
            {
                // the API wrote the job meanwhile (a cancel request): re-read and decide again
            }
        }
    }

    /// <summary>Reload-and-retry: the API may write the same row (a stop request) while the reconciler saves.</summary>
    private async Task FailAsync(MaldexDbContext db, Guid id, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var task = await db.Tasks.SingleAsync(t => t.Id == id, ct).ConfigureAwait(false);
            if (!InFlight.Contains(task.Status)) return;

            var now = time.GetUtcNow();
            task.TransitionTo(RunStatus.Failed, RestartReason, now);
            task.FailureReason = RestartReason;
            task.EndedAt ??= now;
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
            {
                db.ChangeTracker.Clear(); // re-read the row and decide again
            }
        }
    }
}

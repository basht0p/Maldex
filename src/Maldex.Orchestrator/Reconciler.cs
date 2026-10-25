using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Lifecycle;
using Maldex.Data;
using Maldex.Infrastructure.Libvirt;
using Maldex.Orchestrator.Templates;
using Maldex.Orchestrator.Templates.Build;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maldex.Orchestrator;

/// <summary>
/// Runs once when the orchestrator starts. Nothing survives a restart of the control plane, so every VM and
/// network lease still around is a leftover and every task that was mid-run is failed. <c>Processing</c> tasks
/// only need the database, so they are left for the processing worker to resume. Template jobs that had started are
/// failed as interrupted and their working files (and a Build's work directory) deleted; queued ones simply run later.
/// </summary>
public sealed class Reconciler(
    IServiceScopeFactory scopes,
    IHypervisor hypervisor,
    IEgressRouter egress,
    ITemplateStore templates,
    IOptions<LibvirtOptions> libvirt,
    IOptions<BuildOptions> build,
    TimeProvider time,
    ILogger<Reconciler> logger)
{
    public const string RestartReason = "orchestrator restart";

    private const int MaxSaveAttempts = 3;

    private static readonly RunStatus[] InFlight = [RunStatus.Preparing, RunStatus.Running, RunStatus.Collecting];

    private static readonly IReadOnlyList<TemplateJobState> JobsInFlight = TemplateJobStateMachine.StartedStates;

    private static readonly IReadOnlyList<TemplateJobState> ActiveJobs = TemplateJobStateMachine.ActiveStates;

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

        DeleteAgentCopies();

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

        await DeleteOrphanedWorkDirsAsync(db, ct).ConfigureAwait(false);

        _reconciled.TrySetResult();
    }

    /// <summary>The temporary agent copies a template job writes for the disk editor; one left behind is a crash's leftover.</summary>
    private void DeleteAgentCopies()
    {
        var dir = TemplateCoordinator.AgentCopyDir(libvirt.Value);
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, TemplateCoordinator.AgentCopyPrefix + "*"))
            {
                File.Delete(file);
                logger.LogWarning("Deleted leftover agent copy {File}.", file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Deleting the leftover agent copies in {Dir} failed.", dir);
        }
    }

    /// <summary>
    /// The work directories of Builds (<c>&lt;WorkDir&gt;/&lt;stem&gt;</c>) that belong to no active job: a crash's leftovers, as
    /// <see cref="DeleteAgentCopies"/> are. Only directories named like a job stem are touched.
    /// </summary>
    private async Task DeleteOrphanedWorkDirsAsync(MaldexDbContext db, CancellationToken ct)
    {
        var root = Path.GetFullPath(build.Value.WorkDir);
        if (!Directory.Exists(root)) return;
        var active = (await db.TemplateJobs.AsNoTracking().Where(j => ActiveJobs.Contains(j.State)).Select(j => j.Id).ToListAsync(ct).ConfigureAwait(false))
            .Select(TemplateCoordinator.StemOf).ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (!Guid.TryParseExact(name, "N", out var id) || TemplateCoordinator.StemOf(id) != name || active.Contains(name)) continue;
                Directory.Delete(dir, recursive: true);
                logger.LogWarning("Deleted leftover build work directory {Dir}.", dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Deleting the leftover build work directories in {Dir} failed.", root);
        }
    }

    private void DeleteWorkDir(Guid jobId)
    {
        var dir = TemplateCoordinator.WorkDirOf(build.Value, jobId);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Deleting the work directory {Dir} of interrupted template job {JobId} failed.", dir, jobId);
        }
    }

    /// <summary>
    /// Deletes the job's files (working copy or sealed, never recorded) and a Build's work directory, then marks it Failed with its system audit row. A Direct
    /// session still open (its egress start audited, no stop) gets its <c>template.egress.stop</c> row in the same save.
    /// </summary>
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
        DeleteWorkDir(id);

        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var job = await db.TemplateJobs.SingleAsync(j => j.Id == id, ct).ConfigureAwait(false);
            if (!JobsInFlight.Contains(job.State)) return;

            var now = time.GetUtcNow();
            if (job.Network == NetworkMode.Direct && await DirectSessionOpenAsync(db, job, ct).ConfigureAwait(false))
                db.AuditLogs.Add(new AuditLog
                {
                    At = now, UserId = null, Action = "template.egress.stop", Target = job.TemplateId, Details = TemplateCoordinator.DirectSessionDetails(job.Id),
                });
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

    private static async Task<bool> DirectSessionOpenAsync(MaldexDbContext db, TemplateJob job, CancellationToken ct)
    {
        var details = TemplateCoordinator.DirectSessionDetails(job.Id);
        var actions = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Target == job.TemplateId && a.Details == details && (a.Action == "template.egress.start" || a.Action == "template.egress.stop"))
            .Select(a => a.Action).ToListAsync(ct).ConfigureAwait(false);
        return actions.Count(a => a == "template.egress.start") > actions.Count(a => a == "template.egress.stop");
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

using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Core.Lifecycle;
using Maldex.Core.Text;
using Maldex.Data;
using Maldex.Data.Templates;
using Maldex.Infrastructure.Libvirt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maldex.Orchestrator.Templates;

/// <summary>
/// Drives one Edit or Refresh job from <c>Queued</c> to a terminal state: copy the base version to a new stem, boot it writable,
/// (Edit) wait for the admin, seal, verify, and record the new version (published unless <see cref="TemplateJob.PublishOnSuccess"/>
/// is false). The job row is polled for the admin's seal and cancel requests, as <see cref="RunCoordinator"/> polls its task.
/// On failure or cancel the VM is destroyed, the lease released and the job's files deleted before the job is marked; no
/// version row is ever written for a job that did not verify.
/// </summary>
/// <remarks>
/// The new version's id is the job's id, so its files (<see cref="StemOf"/>) can be found from the job alone, after a restart too.
/// </remarks>
public sealed class TemplateCoordinator(
    IServiceScopeFactory scopes,
    IHypervisor hypervisor,
    IGuestDiskEditor diskEditor,
    IAgentChannelFactory channels,
    IEgressRouter egress,
    ITemplateStore store,
    IDiskSpaceProbe disk,
    TemplateSealer sealer,
    TemplateVerifier verifier,
    VmSlotGate slotGate,
    IOptions<OrchestratorOptions> orchestrator,
    IOptions<TemplateJobOptions> options,
    IOptions<LibvirtOptions> libvirt,
    TimeProvider time,
    ILogger<TemplateCoordinator> logger,
    ITemplateJobPublisher? publisher = null,
    JobConsoleRegistry? consoles = null)
{
    public const string InterruptedReason = "interrupted";
    public const string CancelledByAdmin = "cancelled by an admin";
    public const string BaseDiskMissing = "the base version's disk is missing";
    public const string AlreadyRunning = "a job is already running for this template";

    private const int MaxSaveAttempts = 5;
    /// <summary>Tries to record a terminal state (the database may be briefly unreachable), waiting 1, 2, 4 … s in between.</summary>
    private const int MaxTerminalSaveAttempts = 6;
    private const int MaxMessageLength = 500, MaxReasonLength = 1000;

    private static readonly TemplateJobState[] Active = TemplateJobStateMachine.ActiveStates;

    /// <summary>The file stem of the job's working copy, and of the version it produces.</summary>
    public static string StemOf(Guid jobId) => jobId.ToString("N");

    /// <summary>What the job holds while it runs; released on every way out.</summary>
    private sealed class Held
    {
        public NetworkLease? Lease;
        public VmHandle? Vm;
        public bool FilesCreated;
        /// <summary>A Direct egress session is open (audited start, no stop yet).</summary>
        public bool DirectOpen;
    }

    /// <summary>Why a job stopped early; the message is safe as the job's failure reason.</summary>
    private sealed class JobFailedException(string message) : Exception(message);

    private abstract record Outcome;

    /// <summary>The job ends Cancelled with this message; <see cref="UserId"/> null when the system discarded it.</summary>
    private sealed record Discarded(string Message, Guid? UserId) : Outcome;

    private sealed record Verified(VerifiedProvenance Provenance, ComponentBundle Bundle) : Outcome;

    /// <summary>
    /// Runs a <c>Queued</c> job to its end. Returns once the job is terminal; on host shutdown (<paramref name="ct"/>) everything
    /// the job held is released and its files deleted, and the job is left for the reconciler to fail as interrupted.
    /// </summary>
    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var catalog = scope.ServiceProvider.GetRequiredService<TemplateCatalog>();

        var job = await db.TemplateJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job is not { State: TemplateJobState.Queued })
        {
            logger.LogWarning("Template job {JobId} is {State}, not Queued; nothing to run.", jobId, job?.State);
            return;
        }

        if (job.CancelRequestedAt is not null)
        {
            await DiscardAsync(db, job, new Discarded(CancelledByAdmin, job.RequestedByUserId), new Held()).ConfigureAwait(false);
            return;
        }

        var baseVersion = await db.TemplateVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == job.BaseVersionId, ct).ConfigureAwait(false);
        var refusal = await RefusalAsync(db, catalog, job, baseVersion, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            await FailAsync(db, job, refusal, new Held()).ConfigureAwait(false);
            return;
        }

        if (!await ClaimAsync(db, jobId, ct).ConfigureAwait(false)) return;

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watcherCts = new CancellationTokenSource();
        var watcher = WatchForCancelAsync(jobId, jobCts, watcherCts.Token);
        var held = new Held();
        try
        {
            var outcome = await ExecuteAsync(db, job, baseVersion!, held, jobCts.Token).ConfigureAwait(false);
            await StopWatchingAsync(watcherCts, watcher).ConfigureAwait(false);
            switch (outcome)
            {
                case Discarded discarded:
                    await DiscardAsync(db, job, discarded, held).ConfigureAwait(false);
                    break;
                case Verified verified:
                    // Both VMs are gone by now; the files stay unless the version cannot be recorded.
                    await ReleaseAsync(job, held, deleteFiles: false).ConfigureAwait(false);
                    if (!await RecordVersionAsync(db, catalog, job, verified.Provenance, verified.Bundle).ConfigureAwait(false))
                        await DiscardAsync(db, job, new Discarded(CancelledByAdmin, job.RequestedByUserId), held).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await StopWatchingAsync(watcherCts, watcher).ConfigureAwait(false);
            if (await ReleaseAsync(job, held, deleteFiles: true).ConfigureAwait(false) is { } egressStop)
                await SaveAuditQuietlyAsync(db, egressStop).ConfigureAwait(false);
            throw; // host shutdown: the reconciler fails the job on the next start
        }
        catch (OperationCanceledException) when (jobCts.IsCancellationRequested)
        {
            await StopWatchingAsync(watcherCts, watcher).ConfigureAwait(false);
            await DiscardAsync(db, job, new Discarded(CancelledByAdmin, job.RequestedByUserId), held).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await StopWatchingAsync(watcherCts, watcher).ConfigureAwait(false);
            var reason = ex switch
            {
                JobFailedException or TemplateSealException or TemplateVerifyException or TemplateAgentInstallException => ex.Message,
                VmStartException => $"the VM could not be started: {ex.Message}",
                EgressUnavailableException => $"egress unavailable: {ex.Message}",
                _ => $"job failed: {GuestText.Clean(ex.Message, 300)}",
            };
            if (ex is not (JobFailedException or TemplateSealException or TemplateVerifyException or TemplateAgentInstallException)) logger.LogError(ex, "Template job {JobId} failed.", jobId);
            else logger.LogWarning("Template job {JobId} failed: {Reason}", jobId, reason);
            await FailAsync(db, job, reason, held).ConfigureAwait(false);
        }
    }

    /// <summary>Why the job may not start, or null. The endpoint refuses these up front; this catches a race.</summary>
    private async Task<string?> RefusalAsync(MaldexDbContext db, TemplateCatalog catalog, TemplateJob job, TemplateVersion? baseVersion, CancellationToken ct)
    {
        if (job.Kind == TemplateJobKind.Edit && job.Network is not (NetworkMode.None or NetworkMode.Direct))
            return $"network {job.Network} is not available for an edit";
        if (baseVersion is null || baseVersion.TemplateId != job.TemplateId) return "the base version does not belong to this template";
        if (baseVersion.Status != TemplateVersionStatus.Ready) return $"the base version v{baseVersion.Number} is {baseVersion.Status}";
        if (!catalog.DiskPresent(baseVersion.FileStem)) return BaseDiskMissing;
        // Another active job blocks this one when it is already past Queued or was queued earlier (ties: the lower id), so of
        // two raced queued jobs the older one runs.
        var others = await db.TemplateJobs.AsNoTracking()
            .Where(j => j.TemplateId == job.TemplateId && j.Id != job.Id && Active.Contains(j.State))
            .Select(j => new { j.Id, j.State, j.CreatedAt }).ToListAsync(ct).ConfigureAwait(false);
        if (others.Any(j => j.State != TemplateJobState.Queued || j.CreatedAt < job.CreatedAt || (j.CreatedAt == job.CreatedAt && j.Id.CompareTo(job.Id) < 0)))
            return AlreadyRunning;

        return LowDiskSpace(disk.FreeMiB(Path.GetFullPath(libvirt.Value.BaseImageDir)), options.Value.MinFreeGiB);
    }

    /// <summary>The refusal for a template copy that would not fit, or null. One wording for the endpoint's up-front check and the coordinator's.</summary>
    public static string? LowDiskSpace(long freeMiB, int requiredGiB) =>
        freeMiB < requiredGiB * 1024L
            ? $"not enough disk space for a template copy: {freeMiB / 1024.0:0.#} GiB free, {requiredGiB} GiB required"
            : null;

    /// <summary>Queued → Preparing. False when the job is no longer Queued (someone else got there first).</summary>
    private async Task<bool> ClaimAsync(MaldexDbContext db, Guid jobId, CancellationToken ct)
    {
        var claimed = false;
        await SaveJobAsync(db, jobId, j =>
        {
            claimed = false;
            if (j.State != TemplateJobState.Queued) return false;
            var now = time.GetUtcNow();
            j.TransitionTo(TemplateJobState.Preparing, "copying the base version", now);
            j.StartedAt = now;
            return claimed = true;
        }, [], ct).ConfigureAwait(false);
        return claimed;
    }

    /// <summary>The job's steps up to a verified disk: <see cref="Verified"/>, or <see cref="Discarded"/> on the idle timeout.</summary>
    private async Task<Outcome> ExecuteAsync(MaldexDbContext db, TemplateJob job, TemplateVersion baseVersion, Held held, CancellationToken ct)
    {
        var o = options.Value;
        var stem = StemOf(job.Id);

        ComponentBundle bundle;
        try
        {
            bundle = await ComponentBundle.LoadAsync(o, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new JobFailedException(ex.Message);
        }

        held.FilesCreated = true; // a partial copy is deleted too
        await store.CreateWorkingCopyAsync(baseVersion.FileStem, stem, ct).ConfigureAwait(false);

        // Whatever agent the base version carries, the working copy boots with the current one (R24: always, one path).
        await SaveJobAsync(db, job.Id, j => { j.Message = "installing the current agent into the working copy"; return true; }, [], ct).ConfigureAwait(false);
        await InstallAgentAsync(stem, bundle, o, ct).ConfigureAwait(false);

        await TakeVmSlotAsync(db, job.Id, ct).ConfigureAwait(false);

        var mode = job.Kind == TemplateJobKind.Edit ? job.Network : NetworkMode.None;
        held.Lease = await egress.CreateAsync(job.Id, mode, null, ct).ConfigureAwait(false);
        if (mode == NetworkMode.Direct)
        {
            held.DirectOpen = true;
            await SaveJobAsync(db, job.Id, _ => true, [Audit(job, job.RequestedByUserId, "template.egress.start", $"job={job.Id}; network=Direct")], ct).ConfigureAwait(false);
        }
        var vmOptions = orchestrator.Value;
        held.Vm = await hypervisor.StartAsync(new VmSpec(job.Id, stem, vmOptions.VmMemoryMiB, vmOptions.VmVCpus, held.Lease.NetworkId, Writable: true), ct).ConfigureAwait(false);

        await SaveJobAsync(db, job.Id, j => { j.Message = "checking the guest agent"; return true; }, [], ct).ConfigureAwait(false);
        await CheckAgentAsync(held.Vm!, bundle, o, ct).ConfigureAwait(false);

        if (job.Kind == TemplateJobKind.Edit)
        {
            await SaveJobAsync(db, job.Id, j =>
            {
                var now = time.GetUtcNow();
                j.TransitionTo(TemplateJobState.Configuring, "waiting for the admin to configure and seal", now);
                j.ConfiguringSince = now;
                return true;
            }, [], ct).ConfigureAwait(false);

            if (await WaitForSealAsync(db, job.Id, ct).ConfigureAwait(false) is { } idle) return idle;

            // Nobody may keep driving the guest while it is sealed (R22): close the job's open consoles and wait, bounded, for them to go.
            if (consoles is not null && !await consoles.CloseAsync(job.Id, o.ConsoleCloseTimeout, ct).ConfigureAwait(false))
                logger.LogWarning("A console of template job {JobId} was still open {Timeout} after it was told to close; sealing anyway.", job.Id, o.ConsoleCloseTimeout);

            // Sealing runs without any network: cut the running guest off before anything is installed.
            await egress.IsolateAsync(held.Lease, ct).ConfigureAwait(false);
            List<AuditLog> egressStop = [];
            if (held.DirectOpen)
            {
                held.DirectOpen = false;
                egressStop.Add(Audit(job, job.RequestedByUserId, "template.egress.stop", $"job={job.Id}; network=Direct"));
            }
            await TransitionAsync(db, job.Id, TemplateJobState.Sealing, "installing the components and shutting down", egressStop, ct).ConfigureAwait(false);
        }
        else
        {
            await TransitionAsync(db, job.Id, TemplateJobState.Sealing, "installing the components and shutting down", [], ct).ConfigureAwait(false);
        }

        // The sealer reads the agent's ready message itself: hand it an untouched channel.
        var channel = await channels.ConnectAsync(held.Vm, ct).ConfigureAwait(false);
        try
        {
            await sealer.SealAsync(channel, held.Vm, stem, bundle, ct).ConfigureAwait(false);
        }
        finally
        {
            await DisposeQuietlyAsync(channel, job.Id).ConfigureAwait(false);
        }

        // The edit VM is off and gone; its lease goes with it.
        await ReleaseGuestAsync(job, held).ConfigureAwait(false);

        await TransitionAsync(db, job.Id, TemplateJobState.Verifying, "verifying the sealed version", [], ct).ConfigureAwait(false);
        var provenance = await verifier.VerifyAsync(stem, bundle, ct).ConfigureAwait(false);
        return new Verified(provenance, bundle);
    }

    /// <summary>Writes the current agent into the working copy's disk, bounded by <see cref="TemplateJobOptions.AgentInstallTimeout"/>.</summary>
    private async Task InstallAgentAsync(string stem, ComponentBundle bundle, TemplateJobOptions o, CancellationToken ct)
    {
        // The bundle's own bytes (what the later checks compare against), not a re-read of AgentExePath, which a rebuild could replace.
        var file = Path.Combine(Path.GetTempPath(), "maldex-agent-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(file, fileOptions))
                await stream.WriteAsync(bundle.AgentExe, ct).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(o.AgentInstallTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await diskEditor.InstallAgentAsync(stem, file, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new JobFailedException($"writing the agent into the working copy did not finish within {o.AgentInstallTimeout.TotalSeconds:0} s");
            }
        }
        finally
        {
            try { File.Delete(file); }
            catch (Exception ex) { logger.LogWarning(ex, "Deleting the temporary agent copy {File} failed.", file); }
        }
    }

    /// <summary>
    /// Proves the booted working copy runs the agent that was written into it: waits for ready, asks <c>provenance</c> and compares the
    /// build id. An agent that is older (refuses the command, never answers) or different fails the job. The channel is closed again
    /// afterwards: the sealer connects its own and reads that connection's ready message (an agent whose host end is not connected
    /// retries its connection and says ready again, which is how a long-idle Edit guest reaches the sealer too).
    /// </summary>
    private async Task CheckAgentAsync(VmHandle vm, ComponentBundle bundle, TemplateJobOptions o, CancellationToken ct)
    {
        IAgentChannel channel;
        try
        {
            channel = await channels.ConnectAsync(vm, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new JobFailedException($"could not connect to the guest agent ({GuestText.Clean(ex.Message, 200)}); the agent could not be checked");
        }

        try
        {
            await using var session = new GuestSession(channel);

            ReadyMessage ready;
            try
            {
                ready = await session.Ready.WaitAsync(o.AgentReadyTimeout, time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new JobFailedException($"the guest agent was not ready within {o.AgentReadyTimeout.TotalSeconds:0} s; the agent could not be checked");
            }
            catch (InvalidOperationException ex)
            {
                throw new JobFailedException($"the guest agent never became ready ({GuestText.Clean(ex.Message, 200)}); the agent could not be checked");
            }

            var stale = $"the working copy still runs agent {GuestText.Clean(ready.AgentVersion, 100)}; the agent could not be updated";
            using var budget = new CancellationTokenSource(o.ProvenanceTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            ProvenanceMessage answer;
            try
            {
                answer = await session.AskProvenanceAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new JobFailedException(stale); // too old to know the command, or hung
            }
            catch (InvalidOperationException)
            {
                throw new JobFailedException(stale); // refused it, or went away
            }

            if (answer.AgentVersion != bundle.AgentBuildId)
                throw new JobFailedException($"the working copy still runs agent {GuestText.Clean(answer.AgentVersion, 100)}; the agent could not be updated");
        }
        finally
        {
            await DisposeQuietlyAsync(channel, vm.TaskId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes a VM slot before anything boots: under the <see cref="VmSlotGate"/> (which the analysis claimer holds around its
    /// own count and claim), counts the busy slots and, when one is free, saves <see cref="TemplateJob.SlotHeldAt"/>. From then
    /// on <see cref="VmSlots"/> counts the job until it is terminal. Until a slot is free the job waits in Preparing.
    /// </summary>
    private async Task TakeVmSlotAsync(MaldexDbContext db, Guid jobId, CancellationToken ct)
    {
        var said = false;
        while (true)
        {
            using (await slotGate.EnterAsync(ct).ConfigureAwait(false))
            {
                if (await VmSlots.BusyAsync(db, ct).ConfigureAwait(false) < orchestrator.Value.MaxConcurrentVms)
                {
                    await SaveJobAsync(db, jobId, j => { j.SlotHeldAt = time.GetUtcNow(); return true; }, [], ct).ConfigureAwait(false);
                    return;
                }
            }
            if (!said)
            {
                await SaveJobAsync(db, jobId, j => { j.Message = "waiting for a free VM slot"; return true; }, [], ct).ConfigureAwait(false);
                said = true;
            }
            await Task.Delay(options.Value.PollInterval, time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls the job until the admin asks to seal (null) or nobody had the console open for <c>ConfigureIdleTimeout</c>
    /// (counted from the later of entering Configuring and the last console connection), which discards the session.
    /// </summary>
    private async Task<Discarded?> WaitForSealAsync(MaldexDbContext db, Guid jobId, CancellationToken ct)
    {
        var o = options.Value;
        while (true)
        {
            var row = await db.TemplateJobs.AsNoTracking().Where(j => j.Id == jobId)
                .Select(j => new { j.SealRequestedAt, j.ConfiguringSince, j.LastConsoleAt }).SingleAsync(ct).ConfigureAwait(false);
            if (row.SealRequestedAt is not null) return null;

            var since = row.LastConsoleAt is { } console && console > row.ConfiguringSince!.Value ? console : row.ConfiguringSince!.Value;
            var left = since + o.ConfigureIdleTimeout - time.GetUtcNow();
            if (left <= TimeSpan.Zero)
                return new Discarded($"discarded after {o.ConfigureIdleTimeout.TotalHours:0.##} h without a console connection", null);
            await Task.Delay(left < o.PollInterval ? left : o.PollInterval, time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels <paramref name="jobCts"/> once the job row carries a cancel request. Runs on its own DbContext.</summary>
    private async Task WatchForCancelAsync(Guid jobId, CancellationTokenSource jobCts, CancellationToken stop)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (await db.TemplateJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.CancelRequestedAt).SingleAsync(stop).ConfigureAwait(false) is not null)
                {
                    await jobCts.CancelAsync().ConfigureAwait(false);
                    return;
                }
                await Task.Delay(options.Value.PollInterval, time, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reading the cancel request of template job {JobId} failed; retrying.", jobId);
                try { await Task.Delay(options.Value.PollInterval, time, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static async Task StopWatchingAsync(CancellationTokenSource stop, Task watcher)
    {
        await stop.CancelAsync().ConfigureAwait(false);
        await watcher.ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts the version and moves the job to Published or Sealed (publishing the version when asked), all in one transaction.
    /// False when a cancel request arrived first: nothing is written then.
    /// </summary>
    private async Task<bool> RecordVersionAsync(MaldexDbContext db, TemplateCatalog catalog, TemplateJob job, VerifiedProvenance provenance, ComponentBundle bundle)
    {
        var ct = CancellationToken.None; // short, and must not be left half-done
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                var row = await db.TemplateJobs.SingleAsync(j => j.Id == job.Id, ct).ConfigureAwait(false);
                if (row.CancelRequestedAt is not null) return false;

                var number = (await db.TemplateVersions.Where(v => v.TemplateId == row.TemplateId).MaxAsync(v => (int?)v.Number, ct).ConfigureAwait(false) ?? 0) + 1;
                var now = time.GetUtcNow();
                var stem = StemOf(row.Id);
                var version = new TemplateVersion
                {
                    Id = row.Id,
                    TemplateId = row.TemplateId,
                    Number = number,
                    ParentVersionId = row.BaseVersionId,
                    Origin = row.Kind == TemplateJobKind.Edit ? TemplateVersionOrigin.Edit : TemplateVersionOrigin.Refresh,
                    FileStem = stem,
                    WindowsEdition = provenance.WindowsEdition,
                    WindowsBuild = provenance.WindowsBuild,
                    OfficePresent = provenance.OfficePresent,
                    AgentVersion = provenance.AgentVersion,
                    SysmonConfigSha256 = bundle.SysmonConfigSha256,
                    MitmCaSha256 = bundle.MitmCaSha256,
                    SealedByUserId = row.RequestedByUserId,
                    SealedAt = now,
                    Note = row.Note,
                    Status = TemplateVersionStatus.Ready,
                    SizeBytes = store.SizeOf(stem),
                };
                db.TemplateVersions.Add(version);

                var message = row.PublishOnSuccess ? $"published as v{number}" : $"sealed as v{number}; not published";
                if (provenance.Note is not null) message += "; " + provenance.Note;
                row.TransitionTo(row.PublishOnSuccess ? TemplateJobState.Published : TemplateJobState.Sealed, Clip(message, MaxMessageLength), now);
                row.ResultVersionId = version.Id;
                db.AuditLogs.Add(Audit(row, row.RequestedByUserId, "template.seal", $"job={row.Id}; v={number}"));
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                if (row.PublishOnSuccess)
                    await catalog.PublishAsync(row.TemplateId, version.Id, ct, new TemplateAudit(row.RequestedByUserId, "template.publish", $"to=v{number}; job={row.Id}")).ConfigureAwait(false);

                await tx.CommitAsync(ct).ConfigureAwait(false);
                await PublishAsync(db, row).ConfigureAwait(false);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
            {
                // The API wrote the job row meanwhile (a console connect, say): roll back and decide again.
            }
        }
    }

    private Task FailAsync(MaldexDbContext db, TemplateJob job, string reason, Held held)
    {
        reason = Clip(reason, MaxReasonLength);
        return EndAsync(db, job, held, TemplateJobState.Failed, Clip(reason, MaxMessageLength), reason,
            Audit(job, null, "template.job.fail", Clip($"job={job.Id}; reason={reason}", MaxReasonLength)));
    }

    /// <summary>
    /// A cancel an admin asked for is audited by the cancel endpoint (with that admin as the actor), so only a system discard
    /// (the idle timeout; <see cref="Discarded.UserId"/> null) writes <c>template.job.cancel</c> here.
    /// </summary>
    private Task DiscardAsync(MaldexDbContext db, TemplateJob job, Discarded discarded, Held held) =>
        EndAsync(db, job, held, TemplateJobState.Cancelled, discarded.Message, null,
            discarded.UserId is null ? Audit(job, null, "template.job.cancel", $"job={job.Id}; reason={discarded.Message}") : null);

    /// <summary>Releases what the job holds (deleting its files), then marks it terminal with its audit row.</summary>
    private async Task EndAsync(MaldexDbContext db, TemplateJob job, Held held, TemplateJobState to, string message, string? failureReason, AuditLog? audit)
    {
        var egressStop = await ReleaseAsync(job, held, deleteFiles: true).ConfigureAwait(false);
        IReadOnlyList<AuditLog> audits = [.. new[] { egressStop, audit }.OfType<AuditLog>()];
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await SaveJobAsync(db, job.Id, j =>
                {
                    if (TemplateJobStateMachine.IsTerminal(j.State)) return false;
                    j.TransitionTo(to, message, time.GetUtcNow());
                    j.FailureReason = failureReason;
                    return true;
                }, audits, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < MaxTerminalSaveAttempts)
            {
                logger.LogWarning(ex, "Recording template job {JobId} as {State} failed (attempt {Attempt}); retrying in {Delay}.", job.Id, to, attempt, delay);
                await Task.Delay(delay, time).ConfigureAwait(false);
                delay *= 2;
            }
            catch (Exception ex)
            {
                // The VM, lease and files are already gone. The row stays non-terminal and keeps counting as a busy slot until
                // the next start, when the reconciler fails it as interrupted; nothing in this process tracks it any more
                // (the worker forgets the run when it returns).
                logger.LogError(ex, "Could not record template job {JobId} as {State}: {Message}; the reconciler fails it on the next start.", job.Id, to, message);
                return;
            }
        }
    }

    /// <summary>
    /// Destroys the VM, releases the lease and (when asked) deletes the job's files; never throws. Returns the egress-stop audit
    /// row of a Direct session that was still open, for the caller to save.
    /// </summary>
    private async Task<AuditLog?> ReleaseAsync(TemplateJob job, Held held, bool deleteFiles)
    {
        await ReleaseGuestAsync(job, held).ConfigureAwait(false);
        if (deleteFiles && held.FilesCreated)
        {
            try
            {
                await store.DeleteAsync(StemOf(job.Id), CancellationToken.None).ConfigureAwait(false);
                held.FilesCreated = false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Deleting the files of template job {JobId} ({Stem}) failed.", job.Id, StemOf(job.Id));
            }
        }
        if (!held.DirectOpen) return null;
        held.DirectOpen = false;
        return Audit(job, job.RequestedByUserId, "template.egress.stop", $"job={job.Id}; network=Direct");
    }

    private async Task ReleaseGuestAsync(TemplateJob job, Held held)
    {
        consoles?.Close(job.Id); // no wait: the VM is about to go, but no console may send input meanwhile
        if (held.Vm is { } vm)
        {
            try
            {
                await hypervisor.DestroyAsync(vm, CancellationToken.None).ConfigureAwait(false);
                held.Vm = null;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Destroying VM {Vm} of template job {JobId} failed.", vm.Name, job.Id);
            }
        }
        if (held.Lease is { } lease && held.Vm is null)
        {
            try
            {
                await egress.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
                held.Lease = null;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Releasing the network lease of template job {JobId} failed.", job.Id);
            }
        }
    }

    private async Task SaveAuditQuietlyAsync(MaldexDbContext db, AuditLog audit)
    {
        try
        {
            db.ChangeTracker.Clear();
            db.AuditLogs.Add(audit);
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record {Action} for template {TemplateId}.", audit.Action, audit.Target);
        }
    }

    private async Task DisposeQuietlyAsync(IAgentChannel channel, Guid jobId)
    {
        try { await channel.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { logger.LogWarning(ex, "Closing the agent channel of template job {JobId} failed.", jobId); }
    }

    private Task TransitionAsync(MaldexDbContext db, Guid jobId, TemplateJobState to, string message, IReadOnlyList<AuditLog> audits, CancellationToken ct) =>
        SaveJobAsync(db, jobId, j =>
        {
            j.TransitionTo(to, message, time.GetUtcNow());
            return true;
        }, audits, ct);

    /// <summary>
    /// Reloads the job, applies <paramref name="change"/> (false: nothing to save) and saves it with <paramref name="audits"/>.
    /// The API writes the same row (seal/cancel requests, console connects), so a conflict reloads and tries again.
    /// </summary>
    private async Task SaveJobAsync(MaldexDbContext db, Guid jobId, Func<TemplateJob, bool> change, IReadOnlyList<AuditLog> audits, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var job = await db.TemplateJobs.SingleAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
            var (stateBefore, messageBefore) = (job.State, job.Message);
            if (!change(job)) return;
            foreach (var audit in audits) db.AuditLogs.Add(new AuditLog { At = audit.At, UserId = audit.UserId, Action = audit.Action, Target = audit.Target, Details = audit.Details });
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                var changed = job.State != stateBefore || job.Message != messageBefore;
                var saved = job;
                db.ChangeTracker.Clear();
                if (changed) await PublishAsync(db, saved).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
            {
            }
        }
    }

    /// <summary>Tells the watchers of the job its new state or message. Best effort: a failed push never fails the job.</summary>
    private async Task PublishAsync(MaldexDbContext db, TemplateJob job)
    {
        if (publisher is null) return;
        try
        {
            var numbers = await db.TemplateVersions.AsNoTracking()
                .Where(v => v.Id == job.BaseVersionId || (job.ResultVersionId != null && v.Id == job.ResultVersionId))
                .Select(v => new { v.Id, v.Number }).ToListAsync(CancellationToken.None).ConfigureAwait(false);
            var baseNumber = numbers.FirstOrDefault(n => n.Id == job.BaseVersionId)?.Number ?? 0;
            int? resultNumber = numbers.FirstOrDefault(n => n.Id == job.ResultVersionId)?.Number;
            await publisher.PublishAsync(TemplateJobDto.From(job, baseNumber, resultNumber, options.Value.ConfigureIdleTimeout), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Publishing the update of template job {JobId} failed.", job.Id);
        }
    }

    private AuditLog Audit(TemplateJob job, Guid? userId, string action, string details) =>
        new() { At = time.GetUtcNow(), UserId = userId, Action = action, Target = job.TemplateId, Details = details };

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

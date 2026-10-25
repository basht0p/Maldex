using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Core.Lifecycle;
using Maldex.Core.Text;
using Maldex.Data;
using Maldex.Data.Templates;
using Maldex.Infrastructure.Isolation;
using Maldex.Infrastructure.Libvirt;
using Maldex.Infrastructure.Net;
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
    IRunNetworkInfo networkInfo,
    IOptions<NetOptions> net,
    IOptions<SelfTestOptions> selfTest,
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

    private static readonly IReadOnlyList<TemplateJobState> Active = TemplateJobStateMachine.ActiveStates;

    /// <summary>Where the temporary copies of the agent exe are written for the disk editor (swept by the reconciler at start).</summary>
    public static string AgentCopyDir(LibvirtOptions libvirt) => Path.Combine(Path.GetFullPath(libvirt.RunDir), "template-agent");

    public const string AgentCopyPrefix = "maldex-agent-";

    /// <summary>The details of a Direct session's <c>template.egress.start</c> and <c>.stop</c> audit rows.</summary>
    public static string DirectSessionDetails(Guid jobId) => $"job={jobId}; network=Direct";

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
            await DiscardAsync(db, job, new Discarded(CancelledByAdmin, job.RequestedByUserId), new Held(), ct).ConfigureAwait(false);
            return;
        }

        var baseVersion = await db.TemplateVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == job.BaseVersionId, ct).ConfigureAwait(false);
        var refusal = await RefusalAsync(db, catalog, job, baseVersion, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            await FailAsync(db, job, refusal, new Held(), ct).ConfigureAwait(false);
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
                    await DiscardAsync(db, job, discarded, held, ct).ConfigureAwait(false);
                    break;
                case Verified verified:
                    // Both VMs are gone by now; the files stay unless the version cannot be recorded.
                    await ReleaseAsync(job, held, deleteFiles: false).ConfigureAwait(false);
                    if (!await RecordVersionOrKeepAsync(db, catalog, job, verified, held).ConfigureAwait(false))
                        await DiscardAsync(db, job, new Discarded(CancelledByAdmin, job.RequestedByUserId), held, ct).ConfigureAwait(false);
                    break;
            }
        }
        // Cancellation is decided by the tokens, never by the exception's type: a cancelled SQL command, say, surfaces as a
        // SqlException ("Operation cancelled by user"), not as an OperationCanceledException.
        catch (Exception) when (ct.IsCancellationRequested)
        {
            await StopWatchingAsync(watcherCts, watcher).ConfigureAwait(false);
            if (await ReleaseAsync(job, held, deleteFiles: true).ConfigureAwait(false) is { } egressStop)
                await SaveAuditQuietlyAsync(db, egressStop).ConfigureAwait(false);
            throw; // host shutdown: the reconciler fails the job on the next start
        }
        catch (Exception) when (jobCts.IsCancellationRequested)
        {
            await StopWatchingAsync(watcherCts, watcher).ConfigureAwait(false);
            await DiscardAsync(db, job, new Discarded(CancelledByAdmin, job.RequestedByUserId), held, ct).ConfigureAwait(false);
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
            await FailAsync(db, job, reason, held, ct).ConfigureAwait(false);
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

        return await DiskSpaceRefusalAsync(db, store, disk, libvirt.Value, options.Value.MinFreeGiB, baseVersion, job.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The refusal for a template copy that would not fit, or null; the endpoint's up-front check and the coordinator's. Required
    /// free space is the base version's size (unknown: 0) plus <paramref name="minFreeGiB"/>, plus the base sizes of the other
    /// jobs still in Preparing, whose copies may be in flight (<paramref name="jobId"/>, the job being checked, is not one of them).
    /// </summary>
    public static async Task<string?> DiskSpaceRefusalAsync(MaldexDbContext db, ITemplateStore store, IDiskSpaceProbe disk, LibvirtOptions libvirt,
        int minFreeGiB, TemplateVersion baseVersion, Guid jobId, CancellationToken ct)
    {
        var copying = await (from j in db.TemplateJobs.AsNoTracking()
                             where j.State == TemplateJobState.Preparing && j.Id != jobId
                             join v in db.TemplateVersions.AsNoTracking() on j.BaseVersionId equals v.Id
                             select v.FileStem).ToListAsync(ct).ConfigureAwait(false);
        var bytes = SizeOrZero(store, baseVersion.FileStem) + copying.Sum(stem => SizeOrZero(store, stem));
        const long MiB = 1024 * 1024;
        var requiredMiB = minFreeGiB * 1024L + (bytes + MiB - 1) / MiB;
        return LowDiskSpace(disk.FreeMiB(Path.GetFullPath(libvirt.BaseImageDir)), requiredMiB);
    }

    private static long SizeOrZero(ITemplateStore store, string stem)
    {
        try
        {
            return store.SizeOf(stem) ?? 0;
        }
        catch (ArgumentException)
        {
            return 0; // a stem the store rejects has no disk
        }
    }

    /// <summary>The refusal wording for a template copy that would not fit, or null.</summary>
    public static string? LowDiskSpace(long freeMiB, long requiredMiB) =>
        freeMiB < requiredMiB
            ? $"not enough disk space for a template copy: {freeMiB / 1024.0:0.#} GiB free, {requiredMiB / 1024.0:0.#} GiB required"
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
            await SaveJobAsync(db, job.Id, _ => true, [Audit(job, job.RequestedByUserId, "template.egress.start", DirectSessionDetails(job.Id))], ct).ConfigureAwait(false);
        }
        var vmOptions = orchestrator.Value;
        held.Vm = await hypervisor.StartAsync(new VmSpec(job.Id, stem, vmOptions.VmMemoryMiB, vmOptions.VmVCpus, held.Lease.NetworkId, Writable: true), ct).ConfigureAwait(false);

        await SaveJobAsync(db, job.Id, j => { j.Message = "checking the guest agent"; return true; }, [], ct).ConfigureAwait(false);
        await CheckAgentAsync(db, job, held.Vm!, mode == NetworkMode.Direct ? held.Lease : null, bundle, o, ct).ConfigureAwait(false);

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
                egressStop.Add(Audit(job, job.RequestedByUserId, "template.egress.stop", DirectSessionDetails(job.Id)));
            }
            await TransitionAsync(db, job.Id, TemplateJobState.Sealing, "installing the components and shutting down", egressStop, ct).ConfigureAwait(false);
        }
        else
        {
            await TransitionAsync(db, job.Id, TemplateJobState.Sealing, "installing the components and shutting down", [], ct).ConfigureAwait(false);
        }

        // A fresh channel for the sealer: it proves the agent answers with its own provenance round trip (no ready needed, R25).
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
        // Under the run directory, not /tmp: the reconciler removes what a crash leaves there.
        var dir = AgentCopyDir(libvirt.Value);
        var file = Path.Combine(dir, AgentCopyPrefix + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            Directory.CreateDirectory(dir);
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
            catch (Exception) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
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
    /// build id. An agent that is older (refuses the command, never answers) or different fails the job. A Direct session then runs
    /// its isolation self-test on the same channel (<paramref name="directLease"/>). The channel is closed again afterwards: the
    /// sealer connects its own and proves liveness with a provenance round trip, never by waiting for ready (R25).
    /// </summary>
    private async Task CheckAgentAsync(MaldexDbContext db, TemplateJob job, VmHandle vm, NetworkLease? directLease, ComponentBundle bundle, TemplateJobOptions o, CancellationToken ct)
    {
        IAgentChannel channel;
        try
        {
            channel = await channels.ConnectAsync(vm, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
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
            ProvenanceMessage answer;
            try
            {
                answer = await session.AskProvenanceWithRetryAsync(o.ProvenanceRetryInterval, o.ProvenanceTimeout, time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new JobFailedException(stale); // too old to know the command, or hung
            }
            catch (InvalidOperationException)
            {
                throw new JobFailedException(stale); // refused it, or went away
            }

            if (answer.AgentVersion != bundle.AgentBuildId)
                throw new JobFailedException($"the working copy still runs agent {GuestText.Clean(answer.AgentVersion, 100)}; the agent could not be updated");

            if (directLease is not null) await DirectSelfTestAsync(db, job, directLease, channel, session.Inbox, ct).ConfigureAwait(false);
        }
        finally
        {
            await DisposeQuietlyAsync(channel, vm.TaskId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The isolation self-test of a Direct session (spec §4), before the admin gets the guest: LAN, host and control plane must be
    /// unreachable and the internet reachable. Fails closed: any other verdict, or an error running it, fails the job.
    /// </summary>
    private async Task DirectSelfTestAsync(MaldexDbContext db, TemplateJob job, NetworkLease lease, IAgentChannel channel, TransferInbox inbox, CancellationToken ct)
    {
        await SaveJobAsync(db, job.Id, j => { j.Message = "checking the isolation of the Direct network"; return true; }, [], ct).ConfigureAwait(false);
        IsolationVerdict verdict;
        try
        {
            var info = await networkInfo.GetAsync(lease, ct).ConfigureAwait(false);
            if (info is null) verdict = IsolationVerdict.Fail("the job's network addressing is unknown");
            else if (info.Mode != NetworkMode.Direct) verdict = IsolationVerdict.Fail($"network mode mismatch: lease {info.Mode}, job Direct");
            else
            {
                var plan = IsolationProbePlan.Build(net.Value, info);
                verdict = selfTest.Value.RequireControlPlaneProbes && !plan.Targets.Any(t => t.Name.StartsWith("control_plane_", StringComparison.Ordinal))
                    ? IsolationVerdict.Fail("no control-plane probe targets configured")
                    : await new SelfTestGate(time, pollInterval: options.Value.PollInterval).RunAsync(channel, inbox, NetworkMode.Direct, null, plan, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "The Direct isolation self-test of template job {JobId} failed unexpectedly.", job.Id);
            verdict = IsolationVerdict.Fail($"self-test error: {GuestText.Clean(ex.Message, 200)}");
        }

        // Audited as an analysis' self-test is (RunCoordinator): the same actions and details, with the job named first.
        var audit = Audit(job, null, verdict.Isolated ? "isolation.selftest.passed" : "isolation.selftest.failed",
            $"job={job.Id}; mode={NetworkMode.Direct}; {(verdict.Isolated ? "guest-reported probes all blocked" : "not isolated: " + GuestText.Clean(verdict.Reason, 500))}");
        await SaveJobAsync(db, job.Id, _ => true, [audit], ct).ConfigureAwait(false);
        if (!verdict.Isolated) throw new JobFailedException($"isolation self-test failed: {GuestText.Clean(verdict.Reason, 500)}");
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

            var left = TemplateJobStateMachine.IdleDeadline(row.ConfiguringSince!.Value, row.LastConsoleAt, o.ConfigureIdleTimeout) - time.GetUtcNow();
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
    /// When the template's current version is no longer the job's base (a rollback or another publish while it ran), the version
    /// is held (Sealed, Pending) instead of published. False when a cancel request arrived first: nothing is written then.
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

                // Locked until the commit, so a rollback or publish cannot slip in between this check and the publish below.
                var currentId = await db.Templates
                    .FromSqlInterpolated($"SELECT * FROM Templates WITH (UPDLOCK, HOLDLOCK) WHERE Id = {row.TemplateId}")
                    .AsNoTracking().Select(t => t.CurrentVersionId).SingleAsync(ct).ConfigureAwait(false);
                // A rollback or another publish while the job ran: the new version is held for an admin, never published over that choice.
                string? superseded = null;
                if (row.PublishOnSuccess && currentId != row.BaseVersionId)
                {
                    var currentNumber = currentId is null ? null
                        : await db.TemplateVersions.AsNoTracking().Where(v => v.Id == currentId).Select(v => (int?)v.Number).SingleOrDefaultAsync(ct).ConfigureAwait(false);
                    superseded = $"not published: the template's current version changed while this job ran ({(currentNumber is { } n ? $"v{n}" : "no version")} is current); publish it from the template page if wanted";
                }
                var publish = row.PublishOnSuccess && superseded is null;

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

                var message = superseded ?? (publish ? $"published as v{number}" : $"sealed as v{number}; not published");
                if (provenance.Note is not null) message += "; " + provenance.Note;
                row.TransitionTo(publish ? TemplateJobState.Published : TemplateJobState.Sealed, Clip(message, MaxMessageLength), now);
                row.ResultVersionId = version.Id;
                db.AuditLogs.Add(Audit(row, row.RequestedByUserId, "template.seal", $"job={row.Id}; v={number}"));
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                if (publish)
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

    /// <summary>
    /// <see cref="RecordVersionAsync"/>, where an error after the commit (the commit's answer lost, say) never costs the files of a
    /// version that was recorded: the version row (id = job id) is looked up again, and when it exists the job is already terminal
    /// (it committed together with the version) and the files stay. When the lookup fails too, the files are kept and logged
    /// rather than deleted; the job then fails as usual.
    /// </summary>
    private async Task<bool> RecordVersionOrKeepAsync(MaldexDbContext db, TemplateCatalog catalog, TemplateJob job, Verified verified, Held held)
    {
        try
        {
            return await RecordVersionAsync(db, catalog, job, verified.Provenance, verified.Bundle).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            bool recorded;
            try
            {
                db.ChangeTracker.Clear();
                recorded = await db.TemplateVersions.AsNoTracking().AnyAsync(v => v.Id == job.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception lookup)
            {
                held.FilesCreated = false;
                logger.LogError(lookup, "Template job {JobId}: recording its version failed and whether it was recorded is unknown; keeping its files ({Stem}).", job.Id, StemOf(job.Id));
                throw new JobFailedException($"recording the new version failed: {GuestText.Clean(ex.Message, 300)}");
            }
            if (!recorded) throw;

            held.FilesCreated = false;
            logger.LogWarning(ex, "Template job {JobId}: its version was recorded although the commit reported an error.", job.Id);
            var row = await db.TemplateJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, CancellationToken.None).ConfigureAwait(false);
            await PublishAsync(db, row).ConfigureAwait(false);
            return true;
        }
    }

    private Task FailAsync(MaldexDbContext db, TemplateJob job, string reason, Held held, CancellationToken shutdown)
    {
        reason = Clip(reason, MaxReasonLength);
        return EndAsync(db, job, held, TemplateJobState.Failed, Clip(reason, MaxMessageLength), reason,
            Audit(job, null, "template.job.fail", Clip($"job={job.Id}; reason={reason}", MaxReasonLength)), shutdown);
    }

    /// <summary>
    /// A cancel an admin asked for is audited by the cancel endpoint (with that admin as the actor), so only a system discard
    /// (the idle timeout; <see cref="Discarded.UserId"/> null) writes <c>template.job.cancel</c> here.
    /// </summary>
    private Task DiscardAsync(MaldexDbContext db, TemplateJob job, Discarded discarded, Held held, CancellationToken shutdown) =>
        EndAsync(db, job, held, TemplateJobState.Cancelled, discarded.Message, null,
            discarded.UserId is null ? Audit(job, null, "template.job.cancel", $"job={job.Id}; reason={discarded.Message}") : null, shutdown);

    /// <summary>
    /// Releases what the job holds (deleting its files), then marks it terminal with its audit row. The save is retried while the
    /// database is unreachable, but not past host shutdown (<paramref name="shutdown"/>): the reconciler ends the job on the next start.
    /// </summary>
    private async Task EndAsync(MaldexDbContext db, TemplateJob job, Held held, TemplateJobState to, string message, string? failureReason, AuditLog? audit,
        CancellationToken shutdown)
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
                try
                {
                    await Task.Delay(delay, time, shutdown).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.LogError("Shutting down before template job {JobId} could be recorded as {State}; the reconciler fails it on the next start.", job.Id, to);
                    return;
                }
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
        return Audit(job, job.RequestedByUserId, "template.egress.stop", DirectSessionDetails(job.Id));
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
                DeleteRunNetDir(net.Value, job.Id, logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Releasing the network lease of template job {JobId} failed.", job.Id);
            }
        }
    }

    /// <summary>A released lease's leftovers (lease record, logs) under <c>run/net/&lt;namespace&gt;</c>, as for analyses. Best effort.</summary>
    internal static void DeleteRunNetDir(NetOptions net, Guid leaseId, ILogger logger)
    {
        try
        {
            var dir = Path.Combine(Path.GetFullPath(net.RunDir), "net", RunNetNames.For(leaseId).Namespace);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the network run directory of lease {LeaseId}.", leaseId);
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

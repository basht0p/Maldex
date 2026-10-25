using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Core.Lifecycle;
using Maldex.Core.Media;
using Maldex.Core.Text;
using Maldex.Data;
using Maldex.Data.Templates;
using Maldex.Infrastructure.Isolation;
using Maldex.Infrastructure.Libvirt;
using Maldex.Infrastructure.Media;
using Maldex.Infrastructure.Net;
using Maldex.Orchestrator.Templates.Build;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maldex.Orchestrator.Templates;

/// <summary>
/// Drives one Edit, Refresh or Build job from <c>Queued</c> to a terminal state: copy the base version to a new stem (Edit, Refresh)
/// or install Windows onto a blank disk from installer media and provision it (Build), boot it writable, (Edit, Build) wait for
/// the admin and check Defender is off, seal, verify, and record the new version (published unless <see cref="TemplateJob.PublishOnSuccess"/>
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
    TemplateInstaller installer,
    IProvisioningMediaBuilder provisioningMedia,
    MediaStore mediaStore,
    IOptions<BuildOptions> buildOptions,
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
    /// <summary>A Build's Configuring message when provisioning reported Defender real-time protection on (Pro media: Tamper Protection).</summary>
    public const string DefenderGuidance = "Defender real-time protection is on. In the console open Windows Security, turn off Tamper Protection, then Real-time protection, then press Seal.";
    /// <summary>The message when a Seal was refused because the pre-seal check found real-time protection still on.</summary>
    public const string DefenderStillOn = "Defender real-time protection is still on; turn it off in the console, then press Seal again.";

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
        /// <summary>A Build's per-job work directory (<see cref="WorkDirOf"/>), deleted on every way out.</summary>
        public string? WorkDir;
    }

    /// <summary>What a Build installs: its installer media, the application media (Office, at most one) and the chosen edition.</summary>
    private sealed record BuildMedia(MediaFile Os, IReadOnlyList<MediaFile> Applications, EditionChoice Edition);

    /// <summary>A Build's work directory: the provisioning ISO and its stage, <c>&lt;WorkDir&gt;/&lt;stem&gt;</c>.</summary>
    public static string WorkDirOf(BuildOptions build, Guid jobId) => Path.Combine(Path.GetFullPath(build.WorkDir), StemOf(jobId));

    private const long GiB = 1024L * 1024 * 1024;

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

        // A Build starts from installer media and loads no base version.
        TemplateVersion? baseVersion = null;
        BuildMedia? buildMedia = null;
        string? refusal;
        if (job.Kind == TemplateJobKind.Build)
        {
            (refusal, buildMedia) = await BuildRefusalAsync(db, job, ct).ConfigureAwait(false);
        }
        else
        {
            if (job.BaseVersionId is { } baseId)
                baseVersion = await db.TemplateVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == baseId, ct).ConfigureAwait(false);
            refusal = await RefusalAsync(db, catalog, job, baseVersion, ct).ConfigureAwait(false);
        }
        if (refusal is not null)
        {
            await FailAsync(db, job, refusal, new Held(), ct).ConfigureAwait(false);
            return;
        }

        var preparing = job.Kind == TemplateJobKind.Build ? "preparing the disk and the provisioning media" : "copying the base version";
        if (!await ClaimAsync(db, jobId, preparing, ct).ConfigureAwait(false)) return;

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watcherCts = new CancellationTokenSource();
        var watcher = WatchForCancelAsync(jobId, jobCts, watcherCts.Token);
        var held = new Held();
        try
        {
            var outcome = await ExecuteAsync(db, job, baseVersion, buildMedia, held, jobCts.Token).ConfigureAwait(false);
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
                _ when IsSafeReason(ex) => ex.Message,
                VmStartException => $"the VM could not be started: {ex.Message}",
                EgressUnavailableException => $"egress unavailable: {ex.Message}",
                _ => $"job failed: {GuestText.Clean(ex.Message, 300)}",
            };
            if (!IsSafeReason(ex)) logger.LogError(ex, "Template job {JobId} failed.", jobId);
            else logger.LogWarning("Template job {JobId} failed: {Reason}", jobId, reason);
            await FailAsync(db, job, reason, held, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The failures whose message is safe as the job's failure reason, used as it is.</summary>
    private static bool IsSafeReason(Exception ex) =>
        ex is JobFailedException or TemplateSealException or TemplateVerifyException or TemplateAgentInstallException
            or TemplateBuildException or EditionNotSupportedException;

    /// <summary>Why the Edit or Refresh job may not start, or null. The endpoint refuses these up front; this catches a race.</summary>
    private async Task<string?> RefusalAsync(MaldexDbContext db, TemplateCatalog catalog, TemplateJob job, TemplateVersion? baseVersion, CancellationToken ct)
    {
        if (job.Kind == TemplateJobKind.Edit && job.Network is not (NetworkMode.None or NetworkMode.Direct))
            return $"network {job.Network} is not available for an edit";
        if (baseVersion is null || baseVersion.TemplateId != job.TemplateId) return "the base version does not belong to this template";
        if (baseVersion.Status != TemplateVersionStatus.Ready) return $"the base version v{baseVersion.Number} is {baseVersion.Status}";
        if (!catalog.DiskPresent(baseVersion.FileStem)) return BaseDiskMissing;
        if (await ActiveJobRefusalAsync(db, job, ct).ConfigureAwait(false) is { } running) return running;

        return await DiskSpaceRefusalAsync(db, store, disk, libvirt.Value, options.Value.MinFreeGiB, buildOptions.Value.DiskGiB, baseVersion, job.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Why the Build may not start (null: it may), and what it installs: its media are Ready and their files present, the first an
    /// installer and the others at most one Office image; the edition is one a build supports; no other job runs for the template;
    /// the disk fits (<see cref="BuildDiskSpaceRefusalAsync"/>). The endpoint refuses these up front; this catches a race.
    /// </summary>
    private async Task<(string? Refusal, BuildMedia? Media)> BuildRefusalAsync(MaldexDbContext db, TemplateJob job, CancellationToken ct)
    {
        if (job.Network != NetworkMode.None) return ($"network {job.Network} is not available for a build", null);

        List<Guid>? ids;
        try
        {
            ids = JsonSerializer.Deserialize<List<Guid>>(job.MediaIds);
        }
        catch (JsonException)
        {
            ids = null;
        }
        if (ids is not { Count: > 0 }) return ("the build names no installer media", null);
        var rows = await db.MediaFiles.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(ct).ConfigureAwait(false);
        var media = new List<MediaFile>();
        foreach (var id in ids)
        {
            if (rows.SingleOrDefault(m => m.Id == id) is not { } row) return ("a media image of the build is no longer in the library", null);
            media.Add(row);
        }

        var os = media[0];
        if (os.Kind != MediaKind.OsInstaller) return ($"{MediaName(os)} is not installer media", null);
        var applications = media.Skip(1).ToList();
        if (applications.Count > 1 || applications.Any(a => a.Kind != MediaKind.Application || MediaContentsJson.Parse(a.Contents)?.Application != "Office"))
            return ("only Office media can be installed by a build", null);
        foreach (var m in media)
            if (MediaRefusal(m) is { } unusable) return (unusable, null);

        if (MediaContentsJson.Parse(os.Contents) is not { } contents) return ($"the installer media {MediaName(os)} has not been inspected", null);
        EditionChoice edition;
        try
        {
            edition = EditionSelector.Choose(contents.Images, job.EditionIndex);
        }
        catch (EditionNotSupportedException ex)
        {
            return (ex.Message, null);
        }

        if (await ActiveJobRefusalAsync(db, job, ct).ConfigureAwait(false) is { } running) return (running, null);
        var lowSpace = await BuildDiskSpaceRefusalAsync(db, store, disk, libvirt.Value, options.Value.MinFreeGiB, buildOptions.Value.DiskGiB, job.Id, ct).ConfigureAwait(false);
        return lowSpace is not null ? (lowSpace, null) : (null, new BuildMedia(os, applications, edition));
    }

    /// <summary>Why the media image cannot be attached (not Ready, or its file is gone), or null.</summary>
    private string? MediaRefusal(MediaFile media)
    {
        var what = media.Kind == MediaKind.OsInstaller ? "installer" : "application";
        if (media.Status != MediaStatus.Ready || media.Sha256 is null) return $"the {what} media {MediaName(media)} is {media.Status}";
        return File.Exists(mediaStore.FinalPath(media.Sha256)) ? null : $"the {what} media {MediaName(media)} is missing";
    }

    private static string MediaName(MediaFile media) => GuestText.Clean(media.FileName, 255);

    /// <summary>
    /// Another active job blocks this one when it is already past Queued or was queued earlier (ties: the lower id), so of two raced
    /// queued jobs the older one runs.
    /// </summary>
    private static async Task<string?> ActiveJobRefusalAsync(MaldexDbContext db, TemplateJob job, CancellationToken ct)
    {
        var others = await db.TemplateJobs.AsNoTracking()
            .Where(j => j.TemplateId == job.TemplateId && j.Id != job.Id && Active.Contains(j.State))
            .Select(j => new { j.Id, j.State, j.CreatedAt }).ToListAsync(ct).ConfigureAwait(false);
        return others.Any(j => j.State != TemplateJobState.Queued || j.CreatedAt < job.CreatedAt || (j.CreatedAt == job.CreatedAt && j.Id.CompareTo(job.Id) < 0))
            ? AlreadyRunning : null;
    }

    /// <summary>
    /// The refusal for a template copy that would not fit, or null; the endpoint's up-front check and the coordinator's. Required
    /// free space is the base version's size (unknown: 0) plus <paramref name="minFreeGiB"/>, plus what the other jobs in flight may
    /// still write (<see cref="InFlightBytesAsync"/>; <paramref name="jobId"/>, the job being checked, is not one of them).
    /// </summary>
    public static async Task<string?> DiskSpaceRefusalAsync(MaldexDbContext db, ITemplateStore store, IDiskSpaceProbe disk, LibvirtOptions libvirt,
        int minFreeGiB, int buildDiskGiB, TemplateVersion baseVersion, Guid jobId, CancellationToken ct)
    {
        var bytes = SizeOrZero(store, baseVersion.FileStem) + await InFlightBytesAsync(db, store, buildDiskGiB, jobId, ct).ConfigureAwait(false);
        return LowDiskSpace(disk.FreeMiB(Path.GetFullPath(libvirt.BaseImageDir)), RequiredMiB(minFreeGiB, bytes));
    }

    /// <summary>
    /// As <see cref="DiskSpaceRefusalAsync"/>, for a Build: its blank disk may grow to <paramref name="diskGiB"/>, plus
    /// <paramref name="minFreeGiB"/>, plus what the other jobs in flight may still write, on the <see cref="LibvirtOptions.BaseImageDir"/> volume.
    /// </summary>
    public static async Task<string?> BuildDiskSpaceRefusalAsync(MaldexDbContext db, ITemplateStore store, IDiskSpaceProbe disk, LibvirtOptions libvirt,
        int minFreeGiB, int diskGiB, Guid jobId, CancellationToken ct)
    {
        var bytes = diskGiB * GiB + await InFlightBytesAsync(db, store, diskGiB, jobId, ct).ConfigureAwait(false);
        return LowDiskSpace(disk.FreeMiB(Path.GetFullPath(libvirt.BaseImageDir)), RequiredMiB(minFreeGiB, bytes));
    }

    /// <summary>
    /// What the other jobs (not <paramref name="jobId"/>) may still write to the base image volume: an Edit or Refresh in Preparing is
    /// copying its base version (its size); a Build in Preparing, Installing or Provisioning has a blank disk that may grow to
    /// <paramref name="buildDiskGiB"/>.
    /// </summary>
    private static async Task<long> InFlightBytesAsync(MaldexDbContext db, ITemplateStore store, int buildDiskGiB, Guid jobId, CancellationToken ct)
    {
        var copying = await (from j in db.TemplateJobs.AsNoTracking()
                             where j.State == TemplateJobState.Preparing && j.Id != jobId
                             join v in db.TemplateVersions.AsNoTracking() on j.BaseVersionId equals (Guid?)v.Id
                             select v.FileStem).ToListAsync(ct).ConfigureAwait(false);
        var builds = await db.TemplateJobs.AsNoTracking()
            .CountAsync(j => j.Kind == TemplateJobKind.Build && j.Id != jobId
                && (j.State == TemplateJobState.Preparing || j.State == TemplateJobState.Installing || j.State == TemplateJobState.Provisioning), ct).ConfigureAwait(false);
        return copying.Sum(stem => SizeOrZero(store, stem)) + builds * buildDiskGiB * GiB;
    }

    private static long RequiredMiB(int minFreeGiB, long bytes)
    {
        const long MiB = 1024 * 1024;
        return minFreeGiB * 1024L + (bytes + MiB - 1) / MiB;
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

    /// <summary>Queued → Preparing, saying <paramref name="message"/>. False when the job is no longer Queued (someone else got there first).</summary>
    private async Task<bool> ClaimAsync(MaldexDbContext db, Guid jobId, string message, CancellationToken ct)
    {
        var claimed = false;
        await SaveJobAsync(db, jobId, j =>
        {
            claimed = false;
            if (j.State != TemplateJobState.Queued) return false;
            var now = time.GetUtcNow();
            j.TransitionTo(TemplateJobState.Preparing, message, now);
            j.StartedAt = now;
            return claimed = true;
        }, [], ct).ConfigureAwait(false);
        return claimed;
    }

    /// <summary>The job's steps up to a verified disk: <see cref="Verified"/>, or <see cref="Discarded"/> on the idle timeout.</summary>
    private async Task<Outcome> ExecuteAsync(MaldexDbContext db, TemplateJob job, TemplateVersion? baseVersion, BuildMedia? buildMedia, Held held, CancellationToken ct)
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

        var configuringMessage = "waiting for the admin to configure and seal";
        if (job.Kind == TemplateJobKind.Build)
        {
            var report = await InstallAndProvisionAsync(db, job, buildMedia!, bundle, held, ct).ConfigureAwait(false);
            if (report.RealTimeProtectionEnabled == true) configuringMessage = DefenderGuidance;
        }
        else
        {
            await CopyAndBootAsync(db, job, baseVersion!, bundle, held, ct).ConfigureAwait(false);
        }

        // From Configuring on, Build and Edit share one path.
        if (job.Kind != TemplateJobKind.Refresh)
        {
            await SaveJobAsync(db, job.Id, j =>
            {
                var now = time.GetUtcNow();
                j.TransitionTo(TemplateJobState.Configuring, configuringMessage, now);
                j.ConfiguringSince = now;
                return true;
            }, [], ct).ConfigureAwait(false);

            if (await WaitForSealWithDefenderOffAsync(db, job, held.Vm!, o, ct).ConfigureAwait(false) is { } idle) return idle;

            // Nobody may keep driving the guest while it is sealed (R22): close the job's open consoles and wait, bounded, for them to go.
            if (consoles is not null && !await consoles.CloseAsync(job.Id, o.ConsoleCloseTimeout, ct).ConfigureAwait(false))
                logger.LogWarning("A console of template job {JobId} was still open {Timeout} after it was told to close; sealing anyway.", job.Id, o.ConsoleCloseTimeout);

            // Sealing runs without any network: cut the running guest off before anything is installed.
            await egress.IsolateAsync(held.Lease!, ct).ConfigureAwait(false);
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
        var vm = held.Vm!;
        var channel = await channels.ConnectAsync(vm, ct).ConfigureAwait(false);
        try
        {
            await sealer.SealAsync(channel, vm, stem, bundle, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Edit and Refresh (Preparing): copies the base version to the job's stem, writes the current agent into it, takes a VM slot and
    /// a lease (Direct for a Direct edit, audited), boots the copy writable and checks its agent (running a Direct session's self-test).
    /// </summary>
    private async Task CopyAndBootAsync(MaldexDbContext db, TemplateJob job, TemplateVersion baseVersion, ComponentBundle bundle, Held held, CancellationToken ct)
    {
        var o = options.Value;
        var stem = StemOf(job.Id);
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
    }

    /// <summary>
    /// Build (Preparing → Installing → Provisioning): creates the blank disk, builds the provisioning ISO in the job's work directory
    /// (it carries the bundle's agent, so nothing is written into the disk), takes a VM slot and a None lease, boots the installer
    /// from its media, lets <see cref="TemplateInstaller"/> install and provision Windows, then boots the disk again without media
    /// (the slot and the lease stay held across the reboot) and checks its agent. Returns what provisioning reported.
    /// </summary>
    private async Task<ProvisionReport> InstallAndProvisionAsync(MaldexDbContext db, TemplateJob job, BuildMedia media, ComponentBundle bundle, Held held, CancellationToken ct)
    {
        var o = options.Value;
        var b = buildOptions.Value;
        var stem = StemOf(job.Id);
        var virtio = Path.GetFullPath(b.VirtioIsoPath);
        if (!File.Exists(virtio)) throw new JobFailedException("the virtio driver ISO is missing; run ./build.sh real-setup");

        held.FilesCreated = true; // a partly created disk is deleted too
        await store.CreateBlankAsync(stem, b.DiskGiB * GiB, libvirt.Value.OvmfVarsTemplatePath, ct).ConfigureAwait(false);

        held.WorkDir = WorkDirOf(b, job.Id);
        var office = media.Applications.Count > 0;
        string provisionIso;
        try
        {
            provisionIso = Path.GetFullPath(await provisioningMedia.BuildAsync(new ProvisioningInputs(bundle, media.Edition.Index, media.Edition.ProductKey, office), held.WorkDir, ct).ConfigureAwait(false));
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new JobFailedException($"the provisioning media could not be built: {GuestText.Clean(ex.Message, 300)}");
        }
        // Only paths Maldex owns are attached: the library's files, the virtio ISO and this job's own provisioning ISO, which must
        // lie in the job's work directory.
        if (Path.GetDirectoryName(provisionIso) != held.WorkDir)
            throw new JobFailedException("the provisioning media was not written to the job's work directory");

        await TakeVmSlotAsync(db, job.Id, ct).ConfigureAwait(false);
        held.Lease = await egress.CreateAsync(job.Id, NetworkMode.None, null, ct).ConfigureAwait(false);

        // Checked again right before the boot: a file can vanish after the job was accepted, and then no VM starts.
        foreach (var m in media.Applications.Prepend(media.Os))
            if (MediaRefusal(m) is { } unusable) throw new JobFailedException(unusable);
        List<string> cdRoms = [mediaStore.FinalPath(media.Os.Sha256!), virtio, provisionIso, .. media.Applications.Select(a => mediaStore.FinalPath(a.Sha256!))];

        var vmOptions = orchestrator.Value;
        held.Vm = await hypervisor.StartAsync(new VmSpec(job.Id, stem, vmOptions.VmMemoryMiB, vmOptions.VmVCpus, held.Lease.NetworkId, Writable: true,
            CdRoms: cdRoms, BootFromCdRom: true), ct).ConfigureAwait(false);
        await TransitionAsync(db, job.Id, TemplateJobState.Installing, InstallingMessage(media.Edition.Name), [], ct).ConfigureAwait(false);

        var report = await installer.InstallAsync(held.Vm, job.Id, office,
            (state, note) => TransitionAsync(db, job.Id, state, note, [], ct), ct).ConfigureAwait(false);

        // The installer powered the guest off; the media go with it. The slot and the lease stay held for the configure boot.
        await hypervisor.DestroyAsync(held.Vm, ct).ConfigureAwait(false);
        held.Vm = null;
        DeleteWorkDir(job, held);

        held.Vm = await hypervisor.StartAsync(new VmSpec(job.Id, stem, vmOptions.VmMemoryMiB, vmOptions.VmVCpus, held.Lease.NetworkId, Writable: true), ct).ConfigureAwait(false);
        await SaveJobAsync(db, job.Id, j => { j.Message = "checking the guest agent"; return true; }, [], ct).ConfigureAwait(false);
        await CheckAgentAsync(db, job, held.Vm, null, bundle, o, ct).ConfigureAwait(false);
        return report;
    }

    /// <summary>"installing Windows 11 Pro" for the edition "Windows 11 Pro" (the edition names already start with "Windows").</summary>
    private static string InstallingMessage(string edition) =>
        edition.StartsWith("Windows ", StringComparison.Ordinal) ? $"installing {edition}" : $"installing Windows {edition}";

    /// <summary>
    /// Waits for the admin's seal (<see cref="WaitForSealAsync"/>) and checks Defender before anything is installed or isolated
    /// (decision 2, Build and Edit): real-time protection on refuses the seal — the request is cleared so the console opens again,
    /// and the admin is told why, and the idle deadline restarts from the refusal (<see cref="TemplateJob.LastConsoleAt"/>), so the
    /// admin has the whole idle timeout to turn it off — and the wait goes on. An unreadable state does not block. A cancel request
    /// found by the refusal ends the job as a cancel does. Returns the discard (idle timeout or cancel), or null to seal.
    /// </summary>
    private async Task<Discarded?> WaitForSealWithDefenderOffAsync(MaldexDbContext db, TemplateJob job, VmHandle vm, TemplateJobOptions o, CancellationToken ct)
    {
        while (true)
        {
            if (await WaitForSealAsync(db, job.Id, ct).ConfigureAwait(false) is { } idle) return idle;
            if (await DefenderCheckAsync(vm, job.Id, o, ct).ConfigureAwait(false) != true) return null;

            var refused = false;
            await SaveJobAsync(db, job.Id, j =>
            {
                refused = false;
                if (j.CancelRequestedAt is not null) return false; // the cancel wins
                j.SealRequestedAt = null;
                j.LastConsoleAt = time.GetUtcNow();
                j.Message = DefenderStillOn;
                return refused = true;
            }, [], ct, publish: true).ConfigureAwait(false);
            if (!refused) return new Discarded(CancelledByAdmin, job.RequestedByUserId);
            logger.LogInformation("Template job {JobId}: seal refused, Defender real-time protection is still on.", job.Id);
        }
    }

    private const string DefenderCheckFailed = "the guest agent did not answer the Defender check; not sealing";

    /// <summary>
    /// The pre-seal Defender check: one provenance round trip on a fresh channel (no ready wait, R25), closed again afterwards.
    /// Returns the guest's real-time protection state (null: unreadable). No answer or a refusal fails the job.
    /// </summary>
    private async Task<bool?> DefenderCheckAsync(VmHandle vm, Guid jobId, TemplateJobOptions o, CancellationToken ct)
    {
        IAgentChannel channel;
        try
        {
            channel = await channels.ConnectAsync(vm, ct).ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Template job {JobId}: connecting for the Defender check failed.", jobId);
            throw new JobFailedException(DefenderCheckFailed);
        }

        try
        {
            await using var session = new GuestSession(channel);
            try
            {
                var answer = await session.AskProvenanceWithRetryAsync(o.ProvenanceRetryInterval, o.ProvenanceTimeout, time, ct).ConfigureAwait(false);
                return answer.RealTimeProtectionEnabled;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Template job {JobId}: the Defender check got no answer.", jobId);
                throw new JobFailedException(DefenderCheckFailed);
            }
        }
        finally
        {
            await DisposeQuietlyAsync(channel, jobId).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes a Build's work directory (the provisioning ISO and its stage). Best effort: never throws.</summary>
    private void DeleteWorkDir(TemplateJob job, Held held)
    {
        if (held.WorkDir is not { } dir) return;
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            held.WorkDir = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Deleting the work directory {Dir} of template job {JobId} failed.", dir, job.Id);
        }
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

                // A Build that may publish learns whether an enabled default exists, holding that answer until the commit (a range lock
                // even when there is none), so of two first builds publishing together only one becomes the default. Taken before the
                // template's own lock, so every Build takes the two in the same order.
                var defaultExists = true;
                if (row.Kind == TemplateJobKind.Build && row.PublishOnSuccess)
                    defaultExists = await db.Templates
                        .FromSqlRaw("SELECT * FROM Templates WITH (UPDLOCK, HOLDLOCK) WHERE IsDefault = 1 AND Enabled = 1")
                        .AsNoTracking().AnyAsync(ct).ConfigureAwait(false);

                // Locked until the commit, so a rollback or publish cannot slip in between this check and the publish below.
                var currentId = await db.Templates
                    .FromSqlInterpolated($"SELECT * FROM Templates WITH (UPDLOCK, HOLDLOCK) WHERE Id = {row.TemplateId}")
                    .AsNoTracking().Select(t => t.CurrentVersionId).SingleAsync(ct).ConfigureAwait(false);
                // A rollback or another publish while the job ran: the new version is held for an admin, never published over that choice.
                // An Edit or Refresh expects its base to be current; a Build what was current when it was queued (null: no version).
                var expectedId = row.Kind == TemplateJobKind.Build ? row.ExpectedCurrentVersionId : row.BaseVersionId;
                string? superseded = null;
                if (row.PublishOnSuccess && currentId != expectedId)
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
                    Origin = row.Kind switch
                    {
                        TemplateJobKind.Edit => TemplateVersionOrigin.Edit,
                        TemplateJobKind.Build => TemplateVersionOrigin.IsoBuild,
                        _ => TemplateVersionOrigin.Refresh,
                    },
                    SourceMediaIds = row.MediaIds,
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
                {
                    await catalog.PublishAsync(row.TemplateId, version.Id, ct, new TemplateAudit(row.RequestedByUserId, "template.publish", $"to=v{number}; job={row.Id}")).ConfigureAwait(false);
                    if (!defaultExists) await MakeFirstDefaultAsync(db, catalog, tx, row, ct).ConfigureAwait(false);
                }

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
    /// The first template built becomes the default, in the version's transaction (SetDefaultAsync joins it). Never fatal: a failure
    /// rolls back to a savepoint taken just before it, so the version is still published, only without the default and its audit row.
    /// </summary>
    private async Task MakeFirstDefaultAsync(MaldexDbContext db, TemplateCatalog catalog, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        TemplateJob row, CancellationToken ct)
    {
        const string savepoint = "first_default";
        await tx.CreateSavepointAsync(savepoint, ct).ConfigureAwait(false);
        try
        {
            if (!await db.Templates.AsNoTracking().AnyAsync(t => t.Id == row.TemplateId && t.Enabled, ct).ConfigureAwait(false)) return;
            await catalog.SetDefaultAsync(row.TemplateId, ct, new TemplateAudit(null, "template.default", "first template")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Template job {JobId}: making {TemplateId} the default template failed; it is published without becoming the default.", row.Id, row.TemplateId);
            await tx.RollbackToSavepointAsync(savepoint, ct).ConfigureAwait(false);
            // Nothing of the failed step may be saved later: drop its pending rows and changes.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is not TemplateJob).ToList())
                entry.State = EntityState.Detached;
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
    /// Destroys the VM, releases the lease, deletes a Build's work directory and (when asked) the job's files; never throws. Returns
    /// the egress-stop audit row of a Direct session that was still open, for the caller to save.
    /// </summary>
    private async Task<AuditLog?> ReleaseAsync(TemplateJob job, Held held, bool deleteFiles)
    {
        await ReleaseGuestAsync(job, held).ConfigureAwait(false);
        DeleteWorkDir(job, held);
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
    /// <param name="publish">Push the saved job to the watchers even when neither its state nor its message changed (a cleared seal request).</param>
    private async Task SaveJobAsync(MaldexDbContext db, Guid jobId, Func<TemplateJob, bool> change, IReadOnlyList<AuditLog> audits, CancellationToken ct, bool publish = false)
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
                var changed = publish || job.State != stateBefore || job.Message != messageBefore;
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
            var baseNumber = job.BaseVersionId is { } baseId ? numbers.FirstOrDefault(n => n.Id == baseId)?.Number ?? 0 : 0;
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

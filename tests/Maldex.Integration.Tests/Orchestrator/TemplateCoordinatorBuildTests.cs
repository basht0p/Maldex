using System.Security.Cryptography;
using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Core.Media;
using Maldex.Data;
using Maldex.Data.Templates;
using Maldex.Infrastructure.Fakes;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Orchestrator;
using Maldex.Orchestrator.Templates;
using Maldex.Orchestrator.Templates.Build;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

/// <summary>The fake hypervisor, recording every start in order; it can hold <see cref="IsRunningAsync"/> until cancelled.</summary>
internal sealed class RecordingHypervisor(FakeHypervisor inner) : IHypervisor
{
    private readonly List<VmSpec> _starts = [];
    public IReadOnlyList<VmSpec> Starts { get { lock (_starts) return _starts.ToArray(); } }
    /// <summary>Awaited before each start (a test can look at the world just before a VM boots).</summary>
    public Func<VmSpec, Task>? BeforeStart { get; set; }
    /// <summary>
    /// When set, <see cref="IsRunningAsync"/> waits until its token fires and then fails with an <see cref="IOException"/>, as a
    /// killed <c>virsh</c> would: a cancellation that does not surface as an <see cref="OperationCanceledException"/> (R32).
    /// </summary>
    public volatile bool HangIsRunning;

    public async Task<VmHandle> StartAsync(VmSpec spec, CancellationToken ct)
    {
        if (BeforeStart is not null) await BeforeStart(spec);
        lock (_starts) _starts.Add(spec);
        return await inner.StartAsync(spec, ct);
    }

    public Task DestroyAsync(VmHandle handle, CancellationToken ct) => inner.DestroyAsync(handle, ct);

    public async Task<bool> IsRunningAsync(VmHandle vm, CancellationToken ct)
    {
        if (HangIsRunning)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { throw new IOException("virsh was killed"); }
        }
        return await inner.IsRunningAsync(vm, ct);
    }

    public Task<IReadOnlyList<VmHandle>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
    public Task SendKeyAsync(VmHandle vm, string key, CancellationToken ct) => inner.SendKeyAsync(vm, key, ct);
}

/// <summary>Records every job update pushed to the watchers.</summary>
internal sealed class RecordingJobUpdates : ITemplateJobPublisher
{
    private readonly List<TemplateJobDto> _updates = [];
    public IReadOnlyList<TemplateJobDto> Updates { get { lock (_updates) return _updates.ToArray(); } }
    public Task PublishAsync(TemplateJobDto job, CancellationToken ct)
    {
        lock (_updates) _updates.Add(job);
        return Task.CompletedTask;
    }
}

/// <summary>A <see cref="TemplateJobRig"/> with a media library, build paths of its own and a recording hypervisor.</summary>
internal sealed class BuildRig : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public string Dir { get; } = Directory.CreateTempSubdirectory("maldex-build-").FullName;
    public TemplateJobRig R { get; private set; } = null!;
    public RecordingHypervisor Hv => (RecordingHypervisor)R.H.Get<IHypervisor>();
    public string MediaRoot => Path.Combine(Dir, "media");
    public string WorkDir => Path.Combine(Dir, "build");
    public string JobLogDir => Path.Combine(Dir, "jobs");
    public string VirtioIso => Path.Combine(Dir, "virtio-win.iso");
    public string WorkDirOf(Guid jobId) => Path.Combine(WorkDir, TemplateCoordinator.StemOf(jobId));

    public static readonly MediaContents ConsumerMedia = new(
    [
        new InstallerImage(1, "Windows 11 Home", "Core", "26100", "x64"),
        new InstallerImage(6, "Windows 11 Pro", "Professional", "26100", "x64"),
    ], null);

    public static async Task<BuildRig> CreateAsync(SqlServerFixture sql, IDictionary<string, string?>? extra = null, Action<IServiceCollection>? configure = null)
    {
        var rig = new BuildRig();
        Directory.CreateDirectory(rig.MediaRoot);
        await File.WriteAllBytesAsync(rig.VirtioIso, [], Ct);
        var settings = new Dictionary<string, string?>
        {
            ["Maldex:Media:Root"] = rig.MediaRoot,
            ["Maldex:Templates:Build:WorkDir"] = rig.WorkDir,
            ["Maldex:Templates:Build:JobLogDir"] = rig.JobLogDir,
            ["Maldex:Templates:Build:VirtioIsoPath"] = rig.VirtioIso,
            ["Maldex:Templates:Build:ProvisionPollInterval"] = "00:00:00.1",
        };
        foreach (var (k, v) in extra ?? new Dictionary<string, string?>()) settings[k] = v;
        rig.R = await TemplateJobRig.CreateAsync(sql, settings, services =>
        {
            configure?.Invoke(services);
            services.AddSingleton<FakeHypervisor>();
            services.Replace(ServiceDescriptor.Singleton<IHypervisor>(sp => new RecordingHypervisor(sp.GetRequiredService<FakeHypervisor>())));
        });
        return rig;
    }

    public async Task<MediaFile> SeedMediaAsync(MediaKind kind, string fileName, MediaContents contents, bool writeFile = true, MediaStatus status = MediaStatus.Ready)
    {
        var sha = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var media = new MediaFile
        {
            Id = Guid.NewGuid(), Kind = kind, FileName = fileName, Size = 10, ReceivedBytes = 10, Sha256 = sha, Source = MediaSource.Upload,
            Contents = MediaContentsJson.Serialize(contents), Status = status, AddedByUserId = R.AdminId,
            AddedAt = R.H.Time.GetUtcNow(), LastActivityAt = R.H.Time.GetUtcNow(),
        };
        await using var db = R.H.NewContext();
        db.MediaFiles.Add(media);
        await db.SaveChangesAsync(Ct);
        if (writeFile) await File.WriteAllBytesAsync(MediaPath(media), [], Ct);
        return media;
    }

    public string MediaPath(MediaFile media) => Path.Combine(MediaRoot, media.Sha256 + ".iso");

    public Task<MediaFile> SeedOsMediaAsync(bool writeFile = true) => SeedMediaAsync(MediaKind.OsInstaller, "Win11_25H2_English_x64.iso", ConsumerMedia, writeFile);

    public Task<MediaFile> SeedOfficeAsync() => SeedMediaAsync(MediaKind.Application, "ProPlus2024Retail.img", new MediaContents([], "Office"));

    /// <summary>A template with no versions (what the build endpoint creates for a new template); optionally win10 stops being the default.</summary>
    public async Task SeedEmptyTemplateAsync(string id, bool clearOtherDefault = false)
    {
        await using var db = R.H.NewContext();
        if (clearOtherDefault) await db.Templates.ExecuteUpdateAsync(s => s.SetProperty(t => t.IsDefault, false), Ct);
        db.Templates.Add(new Template { Id = id, DisplayName = id, CreatedAt = R.H.Time.GetUtcNow() });
        await db.SaveChangesAsync(Ct);
    }

    public async Task<Guid> SeedBuildAsync(string templateId, IReadOnlyList<MediaFile> media, int? editionIndex = null, Guid? expectedCurrent = null,
        bool publish = true, TemplateJobState state = TemplateJobState.Queued)
    {
        await using var db = R.H.NewContext();
        var job = new TemplateJob
        {
            Id = Guid.NewGuid(), Kind = TemplateJobKind.Build, TemplateId = templateId, MediaIds = JsonSerializer.Serialize(media.Select(m => m.Id)),
            EditionIndex = editionIndex, ExpectedCurrentVersionId = expectedCurrent, Network = NetworkMode.None, State = state,
            RequestedByUserId = R.AdminId, CreatedAt = R.H.Time.GetUtcNow(), PublishOnSuccess = publish, Note = "first build",
        };
        db.TemplateJobs.Add(job);
        await db.SaveChangesAsync(Ct);
        return job.Id;
    }

    public async Task<List<TemplateVersion>> VersionsOfAsync(string templateId)
    {
        await using var db = R.H.NewContext();
        return (await db.TemplateVersions.AsNoTracking().Where(v => v.TemplateId == templateId).ToListAsync(Ct)).OrderBy(v => v.Number).ToList();
    }

    public async Task<Template> TemplateAsync(string id)
    {
        await using var db = R.H.NewContext();
        return await db.Templates.AsNoTracking().SingleAsync(t => t.Id == id, Ct);
    }

    /// <summary>Advances fake time until the job row satisfies <paramref name="done"/> (fails if the run ends first).</summary>
    public async Task PumpUntilAsync(Guid id, Func<TemplateJob, bool> done, Task run, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; ; i++)
        {
            if (i % 10 == 0)
            {
                var job = await R.JobAsync(id);
                if (done(job)) return;
                if (run.IsCompleted) { await run; Assert.Fail($"The job ended in {job.State} ({job.Message}) before {what}."); }
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"Never {what} (now {job.State}: {job.Message}).");
            }
            R.H.Time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1, Ct);
        }
    }

    /// <summary>Nothing of a failed or cancelled Build is left: no VM, lease, slot, work directory or version; its disk deleted.</summary>
    public async Task AssertNothingLeftAsync(Guid id, string templateId)
    {
        Assert.Empty(await R.Hypervisor.ListAsync(Ct));
        Assert.Empty(await R.Egress.ListAsync(Ct));
        Assert.Contains(TemplateCoordinator.StemOf(id), R.Store.Deleted);
        Assert.False(Directory.Exists(WorkDirOf(id)), "the work directory was left behind");
        Assert.Empty(await VersionsOfAsync(templateId));
        Assert.Null((await R.JobAsync(id)).ResultVersionId);
        await using var db = R.H.NewContext();
        Assert.Equal(0, await VmSlots.BusyAsync(db, Ct));
    }

    public async ValueTask DisposeAsync()
    {
        await R.DisposeAsync();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

[Collection("sql")]
public class TemplateCoordinatorBuildTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<string> DistinctStates(IEnumerable<TemplateJobDto> updates) =>
        updates.Select(u => u.State).Aggregate(new List<string>(), (all, s) => { if (all.Count == 0 || all[^1] != s) all.Add(s); return all; });

    [Fact]
    public async Task Build_EndToEnd_IntoAnEmptyTemplate_PublishesAnIsoBuild_AndTheFirstTemplateBecomesTheDefault()
    {
        var publisher = new RecordingJobUpdates();
        await using var rig = await BuildRig.CreateAsync(sql, configure: s => s.AddSingleton<ITemplateJobPublisher>(publisher));
        await rig.SeedEmptyTemplateAsync("win11", clearOtherDefault: true);
        var os = await rig.SeedOsMediaAsync();
        var id = await rig.SeedBuildAsync("win11", [os]);
        var run = rig.R.Start(id);

        await rig.R.PumpUntilAsync(id, TemplateJobState.Configuring, run);
        var configuring = await rig.R.JobAsync(id);
        Assert.Equal("waiting for the admin to configure and seal", configuring.Message);
        Assert.NotNull(configuring.ConfiguringSince);
        await rig.R.RequestSealAsync(id);
        await rig.R.PumpAsync(run);

        var job = await rig.R.JobAsync(id);
        Assert.Equal(TemplateJobState.Published, job.State);
        Assert.Null(job.FailureReason);
        Assert.Equal(["Preparing", "Installing", "Provisioning", "Configuring", "Sealing", "Verifying", "Published"],
            DistinctStates(publisher.Updates.Where(u => u.Id == id)));
        Assert.Contains(publisher.Updates, u => u.Id == id && u.State == "Preparing" && u.Message == "preparing the disk and the provisioning media");
        Assert.Contains(publisher.Updates, u => u.Id == id && u.State == "Installing" && u.Message == "installing Windows 11 Pro");

        var v1 = Assert.Single(await rig.VersionsOfAsync("win11"));
        Assert.Equal((id, 1, TemplateVersionOrigin.IsoBuild, (Guid?)null), (v1.Id, v1.Number, v1.Origin, v1.ParentVersionId));
        Assert.Equal(JsonSerializer.Serialize(new[] { os.Id }), v1.SourceMediaIds);
        Assert.Equal(TemplateCoordinator.StemOf(id), v1.FileStem);
        Assert.Equal(("Windows 11 Pro", "26300.1", rig.R.AgentBuildId), (v1.WindowsEdition, v1.WindowsBuild, v1.AgentVersion));
        Assert.Equal(TemplateJobRig.Hex(rig.R.SysmonConfig), v1.SysmonConfigSha256);
        Assert.Equal((rig.R.AdminId, "first build"), (v1.SealedByUserId!.Value, v1.Note));
        Assert.Equal(id, job.ResultVersionId);

        var template = await rig.TemplateAsync("win11");
        Assert.Equal(v1.Id, template.CurrentVersionId);
        Assert.True(template.IsDefault); // no default existed: the first template becomes it
        var audits = await rig.R.AuditsAsync();
        var @default = Assert.Single(audits, a => a.Action == "template.default");
        Assert.Equal((null, "win11", "first template"), (@default.UserId, @default.Target, @default.Details));
        Assert.Equal(["template.seal", "template.publish", "template.default"], audits.Select(a => a.Action));

        // The disk was created blank (no working copy), sealed and kept; the work directory is gone, and so are the VMs and the lease.
        var blank = Assert.Single(rig.R.Store.Blanks);
        Assert.Equal((TemplateCoordinator.StemOf(id), 64L * 1024 * 1024 * 1024), (blank.Stem, blank.DiskBytes));
        Assert.Empty(rig.R.Store.WorkingCopies);
        Assert.Equal([TemplateCoordinator.StemOf(id)], rig.R.Store.Sealed);
        Assert.DoesNotContain(TemplateCoordinator.StemOf(id), rig.R.Store.Deleted);
        Assert.False(Directory.Exists(rig.WorkDirOf(id)));
        Assert.Empty(await rig.R.Hypervisor.ListAsync(Ct));
        Assert.Empty(await rig.R.Egress.ListAsync(Ct));
        Assert.Equal("create None", rig.R.Egress.Calls[0]);
        Assert.True(File.Exists(Path.Combine(rig.JobLogDir, id.ToString("N"), "provision.log")));
    }

    [Fact]
    public async Task Build_InstallVm_GetsTheFourCdRomsInOrder_TheConfigureVmNone_AndNoAgentIsWrittenIntoTheDisk()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        rig.R.Guest.ProvenanceOfficePresent = true;
        await rig.SeedEmptyTemplateAsync("win11");
        var os = await rig.SeedOsMediaAsync();
        var office = await rig.SeedOfficeAsync();
        var id = await rig.SeedBuildAsync("win11", [os, office]);
        var run = rig.R.Start(id);
        await rig.R.PumpUntilAsync(id, TemplateJobState.Configuring, run);

        var builds = rig.Hv.Starts.Where(s => s.TaskId == id).ToList();
        Assert.Equal(2, builds.Count);
        var install = builds[0];
        Assert.Equal((TemplateCoordinator.StemOf(id), true, true), (install.DiskStem, install.Writable, install.BootFromCdRom));
        Assert.Equal([rig.MediaPath(os), rig.VirtioIso, Path.Combine(rig.WorkDirOf(id), "provision.iso"), rig.MediaPath(office)], install.CdRoms);
        var configure = builds[1];
        Assert.Equal((TemplateCoordinator.StemOf(id), true, false), (configure.DiskStem, configure.Writable, configure.BootFromCdRom));
        Assert.True(configure.CdRoms is null or { Count: 0 });
        Assert.Empty(rig.R.DiskEditor.Installs); // the provisioning media carries the agent
        Assert.False(Directory.Exists(rig.WorkDirOf(id))); // deleted once the install was done

        await rig.R.RequestSealAsync(id);
        await rig.R.PumpAsync(run);
        var v1 = Assert.Single(await rig.VersionsOfAsync("win11"));
        Assert.Equal(JsonSerializer.Serialize(new[] { os.Id, office.Id }), v1.SourceMediaIds);
        Assert.True(v1.OfficePresent);
    }

    [Fact]
    public async Task Build_IntoExistingTemplate_Publishes()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        var os = await rig.SeedOsMediaAsync();
        var id = await rig.SeedBuildAsync("win10", [os], expectedCurrent: rig.R.V1.Id);
        var run = rig.R.Start(id);
        await rig.R.PumpUntilAsync(id, TemplateJobState.Configuring, run);
        await rig.R.RequestSealAsync(id);
        await rig.R.PumpAsync(run);

        Assert.Equal(TemplateJobState.Published, (await rig.R.JobAsync(id)).State);
        var v2 = (await rig.R.VersionsAsync()).Single(v => v.Number == 2);
        Assert.Equal((id, TemplateVersionOrigin.IsoBuild, (Guid?)null), (v2.Id, v2.Origin, v2.ParentVersionId));
        Assert.Equal(v2.Id, await rig.R.CurrentVersionIdAsync());
        Assert.DoesNotContain(await rig.R.AuditsAsync(), a => a.Action == "template.default"); // a default exists already
    }

    [Fact]
    public async Task Build_CurrentChangedWhileRunning_IsHeldPending()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        var os = await rig.SeedOsMediaAsync();
        var id = await rig.SeedBuildAsync("win10", [os], expectedCurrent: rig.R.V1.Id);
        var run = rig.R.Start(id);
        await rig.R.PumpUntilAsync(id, TemplateJobState.Configuring, run);

        // Another version is published while the build runs.
        Guid other;
        await using (var db = rig.R.H.NewContext())
        {
            var v = rig.R.H.NewVersion("win10", 2);
            db.TemplateVersions.Add(v);
            await db.SaveChangesAsync(Ct);
            other = v.Id;
            await using var scope = rig.R.H.Get<IServiceScopeFactory>().CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TemplateCatalog>().PublishAsync("win10", other, Ct);
        }
        await rig.R.RequestSealAsync(id);
        await rig.R.PumpAsync(run);

        var job = await rig.R.JobAsync(id);
        Assert.Equal(TemplateJobState.Sealed, job.State);
        Assert.StartsWith("not published: the template's current version changed while this job ran (v2 is current)", job.Message);
        Assert.Equal(other, await rig.R.CurrentVersionIdAsync());
        Assert.Equal(TemplateVersionStatus.Ready, (await rig.R.VersionsAsync()).Single(v => v.Id == id).Status);
    }

    [Fact]
    public async Task Build_IntoAnEmptyTemplate_ThatGotAVersionMeanwhile_IsHeldPending()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        await rig.SeedEmptyTemplateAsync("win11");
        var os = await rig.SeedOsMediaAsync();
        var id = await rig.SeedBuildAsync("win11", [os], expectedCurrent: null);
        var run = rig.R.Start(id);
        await rig.R.PumpUntilAsync(id, TemplateJobState.Configuring, run);
        await using (var db = rig.R.H.NewContext())
        {
            var v = rig.R.H.NewVersion("win11", 1);
            db.TemplateVersions.Add(v);
            await db.SaveChangesAsync(Ct);
            await db.Templates.Where(t => t.Id == "win11").ExecuteUpdateAsync(s => s.SetProperty(t => t.CurrentVersionId, v.Id), Ct);
        }
        await rig.R.RequestSealAsync(id);
        await rig.R.PumpAsync(run);

        Assert.Equal(TemplateJobState.Sealed, (await rig.R.JobAsync(id)).State);
    }

    [Fact]
    public async Task Build_SlotHeldFromBeforeTheInstallBoot_ThroughTheReboot()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        var busyAtStarts = new List<int>();
        rig.Hv.BeforeStart = async spec =>
        {
            await using var db = rig.R.H.NewContext();
            var busy = await VmSlots.BusyAsync(db, Ct);
            lock (busyAtStarts) busyAtStarts.Add(busy);
        };
        await rig.SeedEmptyTemplateAsync("win11");
        var id = await rig.SeedBuildAsync("win11", [await rig.SeedOsMediaAsync()]);
        var run = rig.R.Start(id);
        await rig.R.PumpUntilAsync(id, TemplateJobState.Configuring, run);

        Assert.Equal([1, 1], busyAtStarts); // counted before the install boot and still across the reboot
        await using (var db = rig.R.H.NewContext()) Assert.Equal(1, await VmSlots.BusyAsync(db, Ct));
        Assert.Equal(["create None"], rig.R.Egress.Calls); // one lease for both boots

        await rig.R.RequestCancelAsync(id);
        await rig.R.PumpAsync(run);
        await rig.AssertNothingLeftAsync(id, "win11");
    }

    [Fact]
    public async Task Build_MediaFileMissingUpFront_FailsBeforeAnyWork()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        await rig.SeedEmptyTemplateAsync("win11");
        var os = await rig.SeedOsMediaAsync(writeFile: false);
        var id = await rig.SeedBuildAsync("win11", [os]);

        await rig.R.PumpAsync(rig.R.Start(id));

        var job = await rig.R.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal($"the installer media {os.FileName} is missing", job.FailureReason);
        Assert.Empty(rig.R.Store.Blanks);
        Assert.Empty(rig.Hv.Starts);
    }

    [Fact]
    public async Task Build_MediaFileMissing_FailsAtPreparing_NoVm()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        await rig.SeedEmptyTemplateAsync("win11");
        var os = await rig.SeedOsMediaAsync();
        var id = await rig.SeedBuildAsync("win11", [os]);
        // The file disappears after the job started (deleted outside Maldex, say), just before the install boot.
        rig.R.Egress.BeforeCreate = _ => { File.Delete(rig.MediaPath(os)); return Task.CompletedTask; };

        await rig.R.PumpAsync(rig.R.Start(id));

        var job = await rig.R.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal($"the installer media {os.FileName} is missing", job.FailureReason);
        Assert.Empty(rig.Hv.Starts);
        Assert.Single(rig.R.Store.Blanks);
        await rig.AssertNothingLeftAsync(id, "win11");
    }

    [Fact]
    public async Task Build_HomeEdition_Fails()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        await rig.SeedEmptyTemplateAsync("win11");
        var id = await rig.SeedBuildAsync("win11", [await rig.SeedOsMediaAsync()], editionIndex: 1);

        await rig.R.PumpAsync(rig.R.Start(id));

        var job = await rig.R.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("Windows 11 Home is not supported: it has no KMS client setup key and ignores the policies Maldex sets", job.FailureReason);
        Assert.Empty(rig.R.Store.Blanks);
        Assert.Empty(rig.Hv.Starts);
    }

    [Fact]
    public async Task Build_LowDisk_Refused_CountingDiskGiB_TheMargin_AndAnInFlightBuild()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        await rig.SeedEmptyTemplateAsync("win11");
        await rig.SeedEmptyTemplateAsync("win12");
        var os = await rig.SeedOsMediaAsync();
        await rig.SeedBuildAsync("win12", [os], state: TemplateJobState.Installing); // another build, its disk growing
        var id = await rig.SeedBuildAsync("win11", [os]);
        rig.R.Disk.Free = (64 + 30 + 64) * 1024 - 1; // one MiB short of DiskGiB + MinFreeGiB + the other build's DiskGiB

        await rig.R.PumpAsync(rig.R.Start(id));

        var job = await rig.R.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("not enough disk space for a template copy: 158 GiB free, 158 GiB required", job.FailureReason);
        Assert.Empty(rig.R.Store.Blanks);
        Assert.Contains(Path.GetFullPath(rig.R.H.Get<IOptions<Maldex.Infrastructure.Libvirt.LibvirtOptions>>().Value.BaseImageDir), rig.R.Disk.Paths);
    }

    [Fact]
    public async Task Build_LowDisk_NotRefused_WithExactlyTheRequiredSpace()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        await rig.SeedEmptyTemplateAsync("win11");
        var id = await rig.SeedBuildAsync("win11", [await rig.SeedOsMediaAsync()]);
        rig.R.Disk.Free = (64 + 30) * 1024;
        var run = rig.R.Start(id);

        await rig.R.PumpUntilAsync(id, TemplateJobState.Configuring, run);
        await rig.R.RequestCancelAsync(id);
        await rig.R.PumpAsync(run);
    }

    [Fact]
    public async Task Build_InstallFails_CleansDiskWorkDirVmAndLease()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        rig.R.Guest.SetProvisionResult("""{"ok":false,"failedSteps":["sysmon"]}""");
        await rig.SeedEmptyTemplateAsync("win11");
        var id = await rig.SeedBuildAsync("win11", [await rig.SeedOsMediaAsync()]);

        await rig.R.PumpAsync(rig.R.Start(id));

        var job = await rig.R.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("provisioning failed in the guest: sysmon; see the provisioning log", job.FailureReason); // the installer's message, as it is
        await rig.AssertNothingLeftAsync(id, "win11");
        Assert.Contains("release", rig.R.Egress.Calls);
        // Only the provisioning log is kept.
        Assert.True(File.Exists(Path.Combine(rig.JobLogDir, id.ToString("N"), "provision.log")));
        var fail = Assert.Single(await rig.R.AuditsAsync());
        Assert.Equal(("template.job.fail", (Guid?)null), (fail.Action, fail.UserId));
    }

    [Fact]
    public async Task Build_VmStopsDuringInstall_FailsWithTheInstallersReason()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        rig.R.Channels.Silent = _ => true; // Setup never gets as far as the agent
        await rig.SeedEmptyTemplateAsync("win11");
        var id = await rig.SeedBuildAsync("win11", [await rig.SeedOsMediaAsync()]);
        var run = rig.R.Start(id);
        await rig.R.PumpUntilAsync(id, TemplateJobState.Installing, run);
        await rig.R.Hypervisor.DestroyAsync(VmHandle.For(id), Ct); // the guest powered itself off

        await rig.R.PumpAsync(run);

        Assert.Equal("Windows Setup stopped before the Maldex agent started (check the media and edition)", (await rig.R.JobAsync(id)).FailureReason);
        await rig.AssertNothingLeftAsync(id, "win11");
    }

    [Theory]
    [InlineData(TemplateJobState.Installing, false)]
    [InlineData(TemplateJobState.Installing, true)]
    [InlineData(TemplateJobState.Provisioning, false)]
    [InlineData(TemplateJobState.Provisioning, true)]
    public async Task Build_Cancel_InInstallingOrProvisioning_EndsCancelled_AndCleansUp(TemplateJobState state, bool nonCancellationError)
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        if (state == TemplateJobState.Installing) rig.R.Channels.Silent = _ => true; // Setup still running
        else rig.R.Guest.SetProvisionResult(null); // provisioning still running
        await rig.SeedEmptyTemplateAsync("win11");
        var id = await rig.SeedBuildAsync("win11", [await rig.SeedOsMediaAsync()]);
        var run = rig.R.Start(id);
        await rig.R.PumpUntilAsync(id, state, run);
        await rig.R.IdleAsync(5);
        Assert.True(Directory.Exists(rig.WorkDirOf(id))); // the provisioning media is still attached
        rig.Hv.HangIsRunning = nonCancellationError; // the interrupted call fails with an IOException, not an OCE (R32)

        await rig.R.RequestCancelAsync(id);
        await rig.R.PumpAsync(run);

        var job = await rig.R.JobAsync(id);
        Assert.Equal(TemplateJobState.Cancelled, job.State);
        Assert.Null(job.FailureReason);
        await rig.AssertNothingLeftAsync(id, "win11");
    }

    [Fact]
    public async Task Build_Interrupted_ReconcilerDeletesWorkDir_AndSweepsOrphanedOnes()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        await rig.SeedEmptyTemplateAsync("win11");
        await rig.SeedEmptyTemplateAsync("win12");
        var os = await rig.SeedOsMediaAsync();
        var id = await rig.SeedBuildAsync("win11", [os], state: TemplateJobState.Installing);
        var queued = await rig.SeedBuildAsync("win12", [os]);
        Directory.CreateDirectory(rig.WorkDirOf(id));
        await File.WriteAllBytesAsync(Path.Combine(rig.WorkDirOf(id), "provision.iso"), [1], Ct);
        var orphan = Path.Combine(rig.WorkDir, Guid.NewGuid().ToString("N")); // a crash's leftover of a job long gone
        Directory.CreateDirectory(Path.Combine(orphan, "stage"));
        var unrelated = Path.Combine(rig.WorkDir, "notes");
        Directory.CreateDirectory(unrelated);
        Directory.CreateDirectory(rig.WorkDirOf(queued)); // cannot exist for a queued job, but would not be touched

        await rig.R.H.Get<Reconciler>().ReconcileAsync(Ct);

        var job = await rig.R.JobAsync(id);
        Assert.Equal((TemplateJobState.Failed, "interrupted"), (job.State, job.FailureReason));
        Assert.False(Directory.Exists(rig.WorkDirOf(id)));
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(unrelated)); // only job work directories
        Assert.True(Directory.Exists(rig.WorkDirOf(queued))); // an active job's
        Assert.Contains(TemplateCoordinator.StemOf(id), rig.R.Store.Deleted);
    }

    [Fact]
    public async Task Build_ConfiguringMessage_IsTheDefenderGuidance_WhenProvisioningReportedRealTimeProtectionOn()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        rig.R.Guest.ProvenanceRealTimeProtection = true; // Pro media: Tamper Protection kept Defender on
        await rig.SeedEmptyTemplateAsync("win11");
        var id = await rig.SeedBuildAsync("win11", [await rig.SeedOsMediaAsync()]);
        var run = rig.R.Start(id);
        await rig.R.PumpUntilAsync(id, TemplateJobState.Configuring, run);

        Assert.Equal(TemplateCoordinator.DefenderGuidance, (await rig.R.JobAsync(id)).Message);
        Assert.Equal("Defender real-time protection is on. In the console open Windows Security, turn off Tamper Protection, then Real-time protection, then press Seal.",
            TemplateCoordinator.DefenderGuidance);

        await rig.R.RequestCancelAsync(id);
        await rig.R.PumpAsync(run);
    }

    [Fact]
    public async Task Seal_DefenderStillOn_StaysConfigured_ThenSealsWhenOff_Build()
    {
        var publisher = new RecordingJobUpdates();
        await using var rig = await BuildRig.CreateAsync(sql, configure: s => s.AddSingleton<ITemplateJobPublisher>(publisher));
        rig.R.Guest.ProvenanceRealTimeProtection = true;
        await rig.SeedEmptyTemplateAsync("win11");
        var id = await rig.SeedBuildAsync("win11", [await rig.SeedOsMediaAsync()]);
        var run = rig.R.Start(id);
        await rig.R.PumpUntilAsync(id, TemplateJobState.Configuring, run);

        await DefenderStillOnThenOffAsync(rig.R, publisher, id, run);

        Assert.Single(await rig.VersionsOfAsync("win11"));
    }

    /// <summary>
    /// Seal with Defender on: nothing is delivered, isolated or sealed, the seal request is cleared and the guidance pushed; twice (the
    /// second push carries no new message or state). Then Defender off: the next Seal publishes.
    /// </summary>
    internal static async Task DefenderStillOnThenOffAsync(TemplateJobRig rig, RecordingJobUpdates publisher, Guid id, Task run)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var pushesBefore = publisher.Updates.Count(u => u.Id == id);
            var askedBefore = rig.Channels.SentTo(id).OfType<ProvenanceCommand>().Count();
            await rig.RequestSealAsync(id);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var j = await rig.JobAsync(id);
                // The push follows the save, so it is waited for too.
                if (j.SealRequestedAt is null && rig.Channels.SentTo(id).OfType<ProvenanceCommand>().Count() > askedBefore
                    && publisher.Updates.Count(u => u.Id == id) > pushesBefore) break;
                Assert.False(run.IsCompleted, $"the job ended ({j.State}: {j.Message})");
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), "the seal request was never answered");
                rig.H.Time.Advance(TimeSpan.FromMilliseconds(50));
                await Task.Delay(5, Ct);
            }
            var job = await rig.JobAsync(id);
            Assert.Equal((TemplateJobState.Configuring, TemplateCoordinator.DefenderStillOn), (job.State, job.Message));
            Assert.Equal("Defender real-time protection is still on; turn it off in the console, then press Seal again.", TemplateCoordinator.DefenderStillOn);
            Assert.Empty(rig.Guest.AppliedComponentIds); // no component delivered
            Assert.DoesNotContain(rig.Egress.Calls, c => c.StartsWith("isolate", StringComparison.Ordinal));
            Assert.Empty(rig.Store.Sealed);
            Assert.Single(await rig.Hypervisor.ListAsync(Ct)); // the guest still runs for the admin
            var after = publisher.Updates.Where(u => u.Id == id).Skip(pushesBefore);
            Assert.Contains(after, u => u.State == "Configuring" && u.Message == TemplateCoordinator.DefenderStillOn); // pushed to the hub
        }

        rig.Guest.ProvenanceRealTimeProtection = false; // the admin turned it off
        await rig.RequestSealAsync(id);
        await rig.PumpAsync(run);
        Assert.Equal(TemplateJobState.Published, (await rig.JobAsync(id)).State);
        Assert.Equal(3, rig.Guest.AppliedComponentIds.Count);
    }

    [Fact]
    public async Task Seal_DefenderUnreadable_Seals()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        await rig.SeedEmptyTemplateAsync("win11");
        var id = await rig.SeedBuildAsync("win11", [await rig.SeedOsMediaAsync()]);
        var run = rig.R.Start(id);
        await rig.R.PumpUntilAsync(id, TemplateJobState.Configuring, run);
        rig.R.Guest.ProvenanceRealTimeProtection = null; // the agent could not read the state

        await rig.R.RequestSealAsync(id);
        await rig.R.PumpAsync(run);

        Assert.Equal(TemplateJobState.Published, (await rig.R.JobAsync(id)).State);
    }

    [Fact]
    public async Task Seal_DefenderCheckUnanswered_FailsTheJob_WithoutSealing()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        await rig.SeedEmptyTemplateAsync("win11");
        var id = await rig.SeedBuildAsync("win11", [await rig.SeedOsMediaAsync()]);
        var run = rig.R.Start(id);
        await rig.R.PumpUntilAsync(id, TemplateJobState.Configuring, run);
        rig.R.Guest.SilentProvenance = true;

        await rig.R.RequestSealAsync(id);
        await rig.R.PumpAsync(run);

        var job = await rig.R.JobAsync(id);
        Assert.Equal((TemplateJobState.Failed, "the guest agent did not answer the Defender check; not sealing"), (job.State, job.FailureReason));
        Assert.Empty(rig.R.Guest.AppliedComponentIds);
        Assert.DoesNotContain(rig.R.Egress.Calls, c => c.StartsWith("isolate", StringComparison.Ordinal));
        await rig.AssertNothingLeftAsync(id, "win11");
    }

    [Fact]
    public async Task Cancel_DuringTheDefenderCheck_EndsCancelled_NotFailed()
    {
        await using var rig = await BuildRig.CreateAsync(sql);
        await rig.SeedEmptyTemplateAsync("win11");
        var id = await rig.SeedBuildAsync("win11", [await rig.SeedOsMediaAsync()]);
        var run = rig.R.Start(id);
        await rig.R.PumpUntilAsync(id, TemplateJobState.Configuring, run);
        rig.R.Guest.SilentProvenance = true; // the check waits for its answer
        var asked = rig.R.Channels.SentTo(id).OfType<ProvenanceCommand>().Count();

        await rig.R.RequestSealAsync(id);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (rig.R.Channels.SentTo(id).OfType<ProvenanceCommand>().Count() == asked)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "the Defender check never started");
            rig.R.H.Time.Advance(TimeSpan.FromMilliseconds(20)); // enough for the seal poll, far less than the check's timeout
            await Task.Delay(5, Ct);
        }
        await rig.R.RequestCancelAsync(id);
        // Only as much fake time as the watcher needs to see the cancel, far less than ProvenanceTimeout (1 min).
        while (!run.IsCompleted)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), "the job did not end");
            rig.R.H.Time.Advance(TimeSpan.FromMilliseconds(20));
            await Task.Delay(5, Ct);
        }
        await run;

        var job = await rig.R.JobAsync(id);
        Assert.Equal(TemplateJobState.Cancelled, job.State);
        Assert.Null(job.FailureReason);
        await rig.AssertNothingLeftAsync(id, "win11");
    }
}

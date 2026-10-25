using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Data.Templates;
using Maldex.Infrastructure.Fakes;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Orchestrator;
using Maldex.Orchestrator.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

/// <summary>A disk-space probe whose answer the test sets.</summary>
internal sealed class SettableDiskSpace : IDiskSpaceProbe
{
    public long Free { get; set; } = 1_000_000;
    public List<string> Paths { get; } = [];

    public long FreeMiB(string path)
    {
        lock (Paths) Paths.Add(path);
        return Free;
    }
}

/// <summary>The fake template store, with disk sizes a test sets (the fake itself knows no sizes).</summary>
internal sealed class SizedTemplateStore(FakeTemplateStore inner) : ITemplateStore
{
    public System.Collections.Concurrent.ConcurrentDictionary<string, long> Sizes { get; } = new();
    public bool Exists(string fileStem) => inner.Exists(fileStem);
    public long? SizeOf(string fileStem) => Sizes.TryGetValue(fileStem, out var size) ? size : inner.SizeOf(fileStem);
    public Task CreateWorkingCopyAsync(string fromStem, string toStem, CancellationToken ct) => inner.CreateWorkingCopyAsync(fromStem, toStem, ct);
    public Task SealAsync(string fileStem, CancellationToken ct) => inner.SealAsync(fileStem, ct);
    public Task DeleteAsync(string fileStem, CancellationToken ct) => inner.DeleteAsync(fileStem, ct);
}

/// <summary>The fake egress router, recording every call (and the job's state at the moment a lease is isolated).</summary>
internal sealed class RecordingEgress(IEgressRouter inner) : IEgressRouter
{
    private readonly List<string> _calls = [];
    public IReadOnlyList<string> Calls { get { lock (_calls) return _calls.ToArray(); } }
    public Func<Guid, Task<string>>? DescribeAtIsolate { get; set; }
    /// <summary>Awaited before each lease is created (a test can hold a job between taking its slot and booting).</summary>
    public Func<Guid, Task>? BeforeCreate { get; set; }

    private void Add(string call) { lock (_calls) _calls.Add(call); }

    public async Task<NetworkLease> CreateAsync(Guid taskId, NetworkMode mode, string? exitCountry, CancellationToken ct)
    {
        if (BeforeCreate is not null) await BeforeCreate(taskId);
        Add($"create {mode}");
        return await inner.CreateAsync(taskId, mode, exitCountry, ct);
    }

    public async Task IsolateAsync(NetworkLease lease, CancellationToken ct)
    {
        Add("isolate" + (DescribeAtIsolate is null ? "" : " " + await DescribeAtIsolate(lease.TaskId)));
        await inner.IsolateAsync(lease, ct);
    }

    public async Task ReleaseAsync(NetworkLease lease, CancellationToken ct)
    {
        Add("release");
        await inner.ReleaseAsync(lease, ct);
    }

    public Task<IReadOnlyList<NetworkLease>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
}

/// <summary>The fake agent factory, except that the guests <see cref="Silent"/> picks never say ready (a guest still booting).</summary>
internal sealed class SilenceableChannels(IAgentChannelFactory inner, FakeGuest guest, TimeProvider time) : IAgentChannelFactory
{
    public Func<VmHandle, bool>? Silent { get; set; }
    /// <summary>Guests whose connection attempt fails (the agent socket is not there).</summary>
    public Func<VmHandle, bool>? Unreachable { get; set; }

    /// <summary>The next connections to a guest lose their first N provenance requests (the agent is still reconnecting).</summary>
    public int DropProvenanceRequests { get; set; }
    /// <summary>How many connections that applies to (the job's check and sealer connect first; the verification boot is a fresh guest).</summary>
    public int DropOnConnections { get => _dropConnections; set => _dropConnections = value; }
    private int _dropConnections = int.MaxValue;

    private readonly List<(Guid TaskId, ReplayAgentChannel Channel)> _opened = [];

    /// <summary>The commands sent on every channel opened to the VM of <paramref name="taskId"/>.</summary>
    public IReadOnlyList<AgentCommand> SentTo(Guid taskId)
    {
        lock (_opened) return _opened.Where(o => o.TaskId == taskId).SelectMany(o => o.Channel.SentCommands).ToList();
    }

    public async Task<IAgentChannel> ConnectAsync(VmHandle vm, CancellationToken ct)
    {
        if (Unreachable?.Invoke(vm) == true) throw new IOException("the agent socket is not there");
        if (Silent?.Invoke(vm) == true) return new ReplayAgentChannel([], 1, holdOpen: true, time, guest, vm.TaskId);
        var channel = await inner.ConnectAsync(vm, ct);
        if (channel is ReplayAgentChannel replay) lock (_opened) _opened.Add((vm.TaskId, replay));
        return DropProvenanceRequests > 0 && Interlocked.Decrement(ref _dropConnections) >= 0 ? new ReconnectGapChannel((ReplayAgentChannel)channel, DropProvenanceRequests) : channel;
    }
}

/// <summary>
/// An <see cref="OrchestratorHarness"/> set up for template jobs: a component bundle on disk, a replay that only says ready,
/// a fake guest that reports the bundle's agent, recording egress, a settable disk probe and an Admin who asks for the jobs.
/// </summary>
internal sealed class TemplateJobRig : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maldex-tjobs-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public OrchestratorHarness H { get; private set; } = null!;
    public TemplateVersion V1 { get; private set; } = null!;
    public Guid AdminId { get; private set; }
    public byte[] AgentExe { get; } = RandomNumberGenerator.GetBytes(100_000);
    public byte[] SysmonConfig { get; } = "<Sysmon schemaversion=\"4.90\"/>"u8.ToArray();
    public byte[] CaDer { get; private set; } = [];
    public string AgentBuildId => AgentIdentity.BuildId("1.2.3", AgentExe);
    public string AgentExePath => Path.Combine(_dir, "Maldex.Agent.exe");

    public FakeGuest Guest => H.Get<FakeGuest>();
    public FakeGuestDiskEditor DiskEditor => (FakeGuestDiskEditor)H.Get<IGuestDiskEditor>();
    public FakeTemplateStore Store => H.Get<FakeTemplateStore>();
    /// <summary>Disk sizes in bytes, by stem (unset: unknown).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, long> Sizes => ((SizedTemplateStore)H.Get<ITemplateStore>()).Sizes;
    public IHypervisor Hypervisor => H.Get<IHypervisor>();
    public RecordingEgress Egress => (RecordingEgress)H.Get<IEgressRouter>();
    public SilenceableChannels Channels => (SilenceableChannels)H.Get<IAgentChannelFactory>();
    public SettableDiskSpace Disk => (SettableDiskSpace)H.Get<IDiskSpaceProbe>();
    public TemplateCoordinator Coordinator => H.Get<TemplateCoordinator>();
    /// <summary>The run directory (libvirt's and the network router's), private to the rig.</summary>
    public string RunDir => Path.Combine(_dir, "run");
    public string NetDirOf(Guid leaseId) => Path.Combine(RunDir, "net", Maldex.Infrastructure.Net.RunNetNames.For(leaseId).Namespace);

    public static async Task<TemplateJobRig> CreateAsync(SqlServerFixture sql, IDictionary<string, string?>? extra = null, Action<IServiceCollection>? configure = null)
    {
        var rig = new TemplateJobRig();
        Directory.CreateDirectory(rig._dir);
        await File.WriteAllBytesAsync(rig.AgentExePath, rig.AgentExe, Ct);
        await File.WriteAllTextAsync(Path.Combine(rig._dir, "Maldex.Agent.version"), "1.2.3", Ct);
        await File.WriteAllBytesAsync(Path.Combine(rig._dir, "sysmon.xml"), rig.SysmonConfig, Ct);
        using (var key = ECDsa.Create())
        {
            var request = new CertificateRequest("CN=maldex-test-ca", key, HashAlgorithmName.SHA256);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            rig.CaDer = cert.RawData;
            await File.WriteAllTextAsync(Path.Combine(rig._dir, "ca.pem"), cert.ExportCertificatePem(), Ct);
        }
        await File.WriteAllTextAsync(Path.Combine(rig._dir, "ready.jsonl"),
            """{"delayMs":0,"message":{"type":"ready","agentVersion":"0.1.0-fake+000000000000","hostname":"h"}}""" + "\n", Ct);

        var settings = new Dictionary<string, string?>
        {
            ["Maldex:Fake:Replay:Directory"] = rig._dir,
            ["Maldex:Fake:Replay:DefaultFile"] = "ready.jsonl",
            ["Maldex:Fake:Replay:HoldOpen"] = "true",
            ["Maldex:Orchestrator:MinFreeDiskMiB"] = "1",
            ["Maldex:Templates:Jobs:AgentExePath"] = rig.AgentExePath,
            ["Maldex:Templates:Jobs:AgentVersionPath"] = Path.Combine(rig._dir, "Maldex.Agent.version"),
            ["Maldex:Templates:Jobs:SysmonConfigPath"] = Path.Combine(rig._dir, "sysmon.xml"),
            ["Maldex:Templates:Jobs:MitmCaPemPath"] = Path.Combine(rig._dir, "ca.pem"),
            ["Maldex:Templates:Jobs:PollInterval"] = "00:00:00.1",
            // Long enough that only a test's own cancel ends a silent guest's wait.
            ["Maldex:Templates:Jobs:AgentReadyTimeout"] = "01:00:00",
            ["Maldex:Templates:Jobs:VerifyTimeout"] = "01:00:00",
        };
        foreach (var (k, v) in extra ?? new Dictionary<string, string?>()) settings[k] = v;

        rig.H = await OrchestratorHarness.CreateAsync(sql, settings, services =>
        {
            configure?.Invoke(services);
            services.Configure<Maldex.Infrastructure.Libvirt.LibvirtOptions>(o => o.RunDir = rig.RunDir);
            services.Configure<Maldex.Infrastructure.Net.NetOptions>(o => o.RunDir = rig.RunDir);
            services.Replace(ServiceDescriptor.Singleton<IDiskSpaceProbe>(new SettableDiskSpace()));
            services.AddSingleton<FakeTemplateStore>();
            services.Replace(ServiceDescriptor.Singleton<ITemplateStore>(sp => new SizedTemplateStore(sp.GetRequiredService<FakeTemplateStore>())));
            services.Replace(ServiceDescriptor.Singleton<IEgressRouter>(sp =>
                new RecordingEgress(new NoopEgressRouter(sp.GetRequiredService<IOptions<FakeEgressOptions>>(), sp.GetRequiredService<FakeGuest>()))));
            services.Replace(ServiceDescriptor.Singleton<IAgentChannelFactory>(sp =>
                new SilenceableChannels(sp.GetRequiredService<FakeAgentChannelFactory>(), sp.GetRequiredService<FakeGuest>(), sp.GetRequiredService<TimeProvider>())));
        });
        rig.Guest.ProvenanceAgentVersion = rig.AgentBuildId;
        rig.Guest.ProvenanceRealTimeProtection = false;

        await using var db = rig.H.NewContext();
        var admin = new AppUser { Id = Guid.NewGuid(), EntraObjectId = Guid.NewGuid().ToString("N"), DisplayName = "basht0p", Role = UserRole.Admin };
        db.Users.Add(admin);
        await db.SaveChangesAsync(Ct);
        rig.AdminId = admin.Id;
        rig.V1 = await db.TemplateVersions.AsNoTracking().SingleAsync(v => v.TemplateId == "win10", Ct);
        return rig;
    }

    public static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public async Task<Guid> SeedJobAsync(TemplateJobKind kind, NetworkMode network = NetworkMode.None, bool publish = true, string? note = null,
        TemplateJobState state = TemplateJobState.Queued, string templateId = "win10", Guid? baseVersionId = null)
    {
        await using var db = H.NewContext();
        var job = new TemplateJob
        {
            Id = Guid.NewGuid(), Kind = kind, TemplateId = templateId, BaseVersionId = baseVersionId ?? V1.Id, Network = network, State = state,
            RequestedByUserId = AdminId, CreatedAt = H.Time.GetUtcNow(), PublishOnSuccess = publish, Note = note,
        };
        db.TemplateJobs.Add(job);
        await db.SaveChangesAsync(Ct);
        return job.Id;
    }

    public async Task<TemplateJob> JobAsync(Guid id)
    {
        await using var db = H.NewContext();
        return await db.TemplateJobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct);
    }

    /// <summary>Sets a field of the job row as the API would, retrying on the coordinator's concurrent writes.</summary>
    public async Task WriteJobAsync(Guid id, Action<TemplateJob> change)
    {
        await using var db = H.NewContext();
        for (var attempt = 1; ; attempt++)
        {
            var job = await db.TemplateJobs.SingleAsync(j => j.Id == id, Ct);
            change(job);
            try
            {
                await db.SaveChangesAsync(Ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 20)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    public Task RequestSealAsync(Guid id) => WriteJobAsync(id, j => j.SealRequestedAt = H.Time.GetUtcNow());
    public Task RequestCancelAsync(Guid id) => WriteJobAsync(id, j => j.CancelRequestedAt = H.Time.GetUtcNow());

    /// <summary>Starts the coordinator on the job in the background.</summary>
    public Task Start(Guid id) => Task.Run(() => Coordinator.RunAsync(id, Ct), Ct);

    /// <summary>Advances fake time in small steps until <paramref name="run"/> finishes.</summary>
    public async Task PumpAsync(Task run)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!run.IsCompleted)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), "The job did not finish within 60 s of real time.");
            H.Time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1, Ct);
        }
        await run;
    }

    /// <summary>Advances fake time in small steps until the job is in <paramref name="state"/> (fails if the run ends first).</summary>
    public async Task PumpUntilAsync(Guid id, TemplateJobState state, Task? run = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; ; i++)
        {
            if (i % 10 == 0)
            {
                var current = (await JobAsync(id)).State;
                if (current == state) return;
                if (run is { IsCompleted: true }) { await run; Assert.Fail($"The job ended in {current} before reaching {state}."); }
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"The job never reached {state} (now {current}).");
            }
            H.Time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1, Ct);
        }
    }

    /// <summary>Lets the coordinator run for a while of real time, advancing the clock by only a few seconds in all.</summary>
    public async Task IdleAsync(int steps = 30)
    {
        for (var i = 0; i < steps; i++)
        {
            H.Time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5, Ct);
        }
    }

    /// <summary>Ends a seeded Running analysis (it moves on to Processing and frees its VM slot).</summary>
    public async Task EndAnalysisAsync(Guid taskId)
    {
        await using var db = H.NewContext();
        var t = await db.Tasks.SingleAsync(x => x.Id == taskId, Ct);
        t.TransitionTo(RunStatus.Collecting, "test", H.Time.GetUtcNow());
        t.TransitionTo(RunStatus.Processing, "test", H.Time.GetUtcNow());
        await db.SaveChangesAsync(Ct);
    }

    public async Task<List<AuditLog>> AuditsAsync()
    {
        await using var db = H.NewContext();
        return (await db.AuditLogs.AsNoTracking().ToListAsync(Ct)).OrderBy(a => a.Id).ToList();
    }

    public async Task<List<TemplateVersion>> VersionsAsync()
    {
        await using var db = H.NewContext();
        return (await db.TemplateVersions.AsNoTracking().Where(v => v.TemplateId == "win10").ToListAsync(Ct)).OrderBy(v => v.Number).ToList();
    }

    public async Task<Guid?> CurrentVersionIdAsync()
    {
        await using var db = H.NewContext();
        return await db.Templates.AsNoTracking().Where(t => t.Id == "win10").Select(t => t.CurrentVersionId).SingleAsync(Ct);
    }

    /// <summary>Nothing of the job is left behind: no VM, no lease, its files deleted, no new version, the template unchanged.</summary>
    public async Task AssertDiscardedAsync(Guid id)
    {
        Assert.Empty(await Hypervisor.ListAsync(Ct));
        Assert.Empty(await Egress.ListAsync(Ct));
        Assert.Contains(TemplateCoordinator.StemOf(id), Store.Deleted);
        var versions = await VersionsAsync();
        Assert.Equal([V1.Id], versions.Select(v => v.Id));
        Assert.Equal(V1.Id, await CurrentVersionIdAsync());
        Assert.Null((await JobAsync(id)).ResultVersionId);
        await using var db = H.NewContext();
        Assert.Equal(0, await VmSlots.BusyAsync(db, Ct));
    }

    public async ValueTask DisposeAsync()
    {
        await H.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

[Collection("sql")]
public class TemplateCoordinatorTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Refresh_EndToEnd_PublishesTheNextVersion_WithProvenanceAndComponentHashes()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        rig.Guest.ProvenanceOfficePresent = true;
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh, note: "monthly refresh");
        var stem = TemplateCoordinator.StemOf(id);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Published, job.State);
        Assert.Null(job.FailureReason);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.EndedAt);
        Assert.Null(job.ConfiguringSince); // a refresh never configures

        var versions = await rig.VersionsAsync();
        Assert.Equal([1, 2], versions.Select(v => v.Number));
        var v2 = versions[1];
        Assert.Equal(job.ResultVersionId, v2.Id);
        Assert.Equal(rig.V1.Id, v2.ParentVersionId);
        Assert.Equal(TemplateVersionOrigin.Refresh, v2.Origin);
        Assert.Equal(stem, v2.FileStem);
        Assert.Equal(TemplateVersionStatus.Ready, v2.Status);
        Assert.Equal(("Windows 11 Pro", "26300.1", rig.AgentBuildId, (bool?)true), (v2.WindowsEdition, v2.WindowsBuild, v2.AgentVersion, v2.OfficePresent));
        Assert.Equal(TemplateJobRig.Hex(rig.SysmonConfig), v2.SysmonConfigSha256);
        Assert.Equal(TemplateJobRig.Hex(rig.CaDer), v2.MitmCaSha256);
        Assert.Equal(rig.AdminId, v2.SealedByUserId);
        Assert.Equal("monthly refresh", v2.Note);

        // v2 is current, v1 is the previous (rollback target) version.
        Assert.Equal(v2.Id, await rig.CurrentVersionIdAsync());
        var roles = TemplateCatalog.RolesOf(versions, v2.Id);
        Assert.Equal("RollbackTarget", roles[rig.V1.Id]);
        Assert.NotNull(versions[0].CurrentUntil);

        // The base was copied to the new stem, the copy sealed and kept; the sealed disk's VMs are gone, and so is the lease.
        Assert.Equal([(rig.V1.FileStem, stem)], rig.Store.WorkingCopies);
        Assert.Equal([stem], rig.Store.Sealed);
        Assert.DoesNotContain(stem, rig.Store.Deleted);
        Assert.Empty(await rig.Hypervisor.ListAsync(Ct));
        Assert.Empty(await rig.Egress.ListAsync(Ct));
        Assert.Equal("create None", rig.Egress.Calls[0]);
        Assert.Equal(3, rig.Guest.AppliedComponentIds.Count); // components applied once

        var audits = await rig.AuditsAsync();
        Assert.Equal(["template.seal", "template.publish"], audits.Select(a => a.Action));
        Assert.All(audits, a => Assert.Equal((rig.AdminId, "win10"), (a.UserId!.Value, a.Target)));
    }

    [Fact]
    public async Task Edit_WaitsInConfiguring_UntilSealIsRequested_ThenPublishes()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit, note: "installed 7-Zip");
        var run = rig.Start(id);

        await rig.PumpUntilAsync(id, TemplateJobState.Configuring, run);
        var configuring = await rig.JobAsync(id);
        Assert.NotNull(configuring.StartedAt);
        Assert.NotNull(configuring.ConfiguringSince);
        Assert.Single(await rig.Hypervisor.ListAsync(Ct)); // the edit VM runs while the admin works
        await rig.IdleAsync();
        Assert.Equal(TemplateJobState.Configuring, (await rig.JobAsync(id)).State); // it waits for the admin

        await rig.RequestSealAsync(id);
        await rig.PumpAsync(run);

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Published, job.State);
        var v2 = (await rig.VersionsAsync()).Single(v => v.Number == 2);
        Assert.Equal((TemplateVersionOrigin.Edit, "installed 7-Zip", TemplateCoordinator.StemOf(id)), (v2.Origin, v2.Note, v2.FileStem));
        Assert.Equal(v2.Id, await rig.CurrentVersionIdAsync());
        // A None edit isolates nothing extra and audits no egress session.
        Assert.DoesNotContain(await rig.AuditsAsync(), a => a.Action.StartsWith("template.egress"));
    }

    [Fact]
    public async Task Edit_Direct_IsolatesTheLeaseBeforeSealing_AndAuditsTheEgressSession()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit, NetworkMode.Direct);
        rig.Egress.DescribeAtIsolate = async jobId => (await rig.JobAsync(jobId)).State.ToString();
        var run = rig.Start(id);

        await rig.PumpUntilAsync(id, TemplateJobState.Configuring, run);
        await rig.RequestSealAsync(id);
        await rig.PumpAsync(run);

        Assert.Equal(TemplateJobState.Published, (await rig.JobAsync(id)).State);
        // One lease for the edit VM (Direct), cut off while still configuring, i.e. before any sealing step; then the verify VM's own None lease.
        Assert.Equal(["create Direct", "isolate Configuring", "release", "create None", "release"], rig.Egress.Calls);
        var audits = await rig.AuditsAsync();
        Assert.Equal(["template.egress.start", "template.egress.stop", "template.seal", "template.publish"], audits.Select(a => a.Action));
        Assert.All(audits.Take(2), a => Assert.Equal(rig.AdminId, a.UserId));
        Assert.All(audits.Take(2), a => Assert.Contains($"job={id}", a.Details));
    }

    [Fact]
    public async Task Edit_Direct_RunsTheIsolationSelfTest_BeforeTheAdminGetsTheGuest()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit, NetworkMode.Direct);
        var run = rig.Start(id);

        await rig.PumpUntilAsync(id, TemplateJobState.Configuring, run);

        var selfTests = rig.Channels.SentTo(id).OfType<SelfTestCommand>().ToList();
        var selfTest = Assert.Single(selfTests);
        Assert.Equal(rig.H.Get<IOptions<Maldex.Infrastructure.Net.NetOptions>>().Value.DirectProbeUrl, selfTest.EchoUrl);
        Assert.Contains(selfTest.Targets, t => t.Name == "gateway");
        await rig.RequestCancelAsync(id);
        await rig.PumpAsync(run);
    }

    [Fact]
    public async Task Edit_None_RunsNoSelfTest()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit);
        var run = rig.Start(id);

        await rig.PumpUntilAsync(id, TemplateJobState.Configuring, run);

        Assert.Empty(rig.Channels.SentTo(id).OfType<SelfTestCommand>());
        await rig.RequestCancelAsync(id);
        await rig.PumpAsync(run);
    }

    [Fact]
    public async Task Edit_Direct_FailedSelfTest_FailsTheJob_AndDiscardsEverything()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        rig.Guest.SetReachable("gateway"); // the guest can reach the host side of its network
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit, NetworkMode.Direct);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("isolation self-test failed: gateway is reachable from the guest", job.FailureReason);
        Assert.Null(job.ConfiguringSince); // the admin never got the guest
        await rig.AssertDiscardedAsync(id);
        Assert.Equal(["create Direct", "release"], rig.Egress.Calls);
        Assert.Equal(["template.egress.start", "template.egress.stop", "template.job.fail"], (await rig.AuditsAsync()).Select(a => a.Action));
    }

    [Fact]
    public async Task PublishOnSuccessFalse_EndsSealed_WithAPendingVersion()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh, publish: false);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Sealed, job.State);
        var versions = await rig.VersionsAsync();
        var v2 = versions.Single(v => v.Number == 2);
        Assert.Equal(job.ResultVersionId, v2.Id);
        Assert.Equal(TemplateVersionStatus.Ready, v2.Status);
        Assert.Equal(rig.V1.Id, await rig.CurrentVersionIdAsync()); // not published
        Assert.Equal("Pending", TemplateCatalog.RolesOf(versions, rig.V1.Id)[v2.Id]);
        Assert.Equal(["template.seal"], (await rig.AuditsAsync()).Select(a => a.Action));
    }

    [Fact]
    public async Task RollbackWhileARefreshRuns_HoldsTheNewVersionPending_AndTheRolledBackToVersionStaysCurrent()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        // v2 is published over v1 (so v1 is the rollback target), and the refresh works from v2.
        var v2 = rig.H.NewVersion("win10", 2);
        await using (var db = rig.H.NewContext())
        {
            db.TemplateVersions.Add(v2);
            await db.SaveChangesAsync(Ct);
        }
        await using (var scope = rig.H.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<TemplateCatalog>().PublishAsync("win10", v2.Id, Ct);
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh, baseVersionId: v2.Id);
        // While the job prepares its copy, an admin rolls back (v2 is retired) and the retention sweep runs.
        IReadOnlyList<TemplateVersion>? swept = null;
        rig.DiskEditor.OnInstall = async (_, _) =>
        {
            await using var scope = rig.H.Services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<TemplateCatalog>();
            await catalog.RollBackAsync("win10", Ct);
            swept = await catalog.SweepAsync(Ct);
        };

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Sealed, job.State);
        Assert.Equal("not published: the template's current version changed while this job ran (v1 is current); publish it from the template page if wanted", job.Message);
        Assert.Equal(rig.V1.Id, await rig.CurrentVersionIdAsync());
        var versions = await rig.VersionsAsync();
        var v3 = versions.Single(v => v.Number == 3);
        Assert.Equal((job.ResultVersionId, TemplateVersionStatus.Ready, (Guid?)v2.Id), (v3.Id, v3.Status, v3.ParentVersionId));
        Assert.Equal("Pending", TemplateCatalog.RolesOf(versions, rig.V1.Id)[v3.Id]);
        // The job's base was kept while it ran: retired, but its disk not deleted.
        Assert.Empty(swept!);
        Assert.Equal(TemplateVersionStatus.Retired, versions.Single(v => v.Id == v2.Id).Status);
        Assert.DoesNotContain(v2.FileStem, rig.Store.Deleted);
        Assert.Equal(["template.seal"], (await rig.AuditsAsync()).Select(a => a.Action));
    }

    [Fact]
    public async Task Seal_ClosesTheOpenConsole_AndWaitsForIt_BeforeTheGuestIsIsolated()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Templates:Jobs:ConsoleCloseTimeout"] = "01:00:00" });
        var consoles = rig.H.Get<JobConsoleRegistry>();
        rig.Egress.DescribeAtIsolate = jobId => Task.FromResult($"consoles={consoles.OpenCount(jobId)}");
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit);
        var run = rig.Start(id);
        await rig.PumpUntilAsync(id, TemplateJobState.Configuring, run);

        // An open console that takes a moment (real time) to wind down once it is told to close.
        var console = consoles.Register(id);
        var winding = Task.Run(async () =>
        {
            await Task.Delay(Timeout.Infinite, console.Closing).ContinueWith(_ => { }, TaskScheduler.Default);
            await Task.Delay(150, Ct);
            console.Dispose();
        }, Ct);
        await rig.RequestSealAsync(id);
        await rig.PumpAsync(run);
        await winding;

        Assert.True(console.Closing.IsCancellationRequested);
        Assert.Contains("isolate consoles=0", rig.Egress.Calls); // gone before the guest was cut off and sealed
        Assert.Equal(TemplateJobState.Published, (await rig.JobAsync(id)).State);
    }

    [Fact]
    public async Task Seal_WithAConsoleThatWillNotClose_ProceedsAfterTheBoundedWait()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Templates:Jobs:ConsoleCloseTimeout"] = "00:00:05" });
        var consoles = rig.H.Get<JobConsoleRegistry>();
        rig.Egress.DescribeAtIsolate = jobId => Task.FromResult($"consoles={consoles.OpenCount(jobId)}");
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit);
        var run = rig.Start(id);
        await rig.PumpUntilAsync(id, TemplateJobState.Configuring, run);
        using var stuck = consoles.Register(id); // never disposed by its "tunnel"
        await rig.RequestSealAsync(id);

        await rig.PumpAsync(run);

        Assert.True(stuck.Closing.IsCancellationRequested); // it was told to close
        Assert.Contains("isolate consoles=1", rig.Egress.Calls); // and the seal went on anyway
        Assert.Equal(TemplateJobState.Published, (await rig.JobAsync(id)).State);
    }

    [Fact]
    public async Task Cancel_SignalsTheOpenConsole_BeforeTheVmIsDestroyed()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var consoles = rig.H.Get<JobConsoleRegistry>();
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit);
        var run = rig.Start(id);
        await rig.PumpUntilAsync(id, TemplateJobState.Configuring, run);
        using var console = consoles.Register(id);

        await rig.RequestCancelAsync(id);
        await rig.PumpAsync(run);

        Assert.True(console.Closing.IsCancellationRequested);
        Assert.Equal(TemplateJobState.Cancelled, (await rig.JobAsync(id)).State);
    }

    private sealed class RecordingJobPublisher : ITemplateJobPublisher
    {
        public List<TemplateJobDto> Updates { get; } = [];
        public Task PublishAsync(TemplateJobDto job, CancellationToken ct)
        {
            lock (Updates) Updates.Add(job);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Edit_PublishesEveryStateChangeToTheWatchers_WithTheIdleDeadlineWhileConfiguring()
    {
        var publisher = new RecordingJobPublisher();
        await using var rig = await TemplateJobRig.CreateAsync(sql, configure: s => s.AddSingleton<ITemplateJobPublisher>(publisher));
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit);
        var run = rig.Start(id);
        await rig.PumpUntilAsync(id, TemplateJobState.Configuring, run);
        await rig.RequestSealAsync(id);

        await rig.PumpAsync(run);

        TemplateJobDto[] updates;
        lock (publisher.Updates) updates = [.. publisher.Updates.Where(u => u.Id == id)];
        var states = updates.Select(u => u.State).Aggregate(new List<string>(), (all, s) => { if (all.Count == 0 || all[^1] != s) all.Add(s); return all; });
        Assert.Equal(["Preparing", "Configuring", "Sealing", "Verifying", "Published"], states);
        var configuring = updates.First(u => u.State == "Configuring");
        var job = await rig.JobAsync(id);
        Assert.Equal(job.ConfiguringSince + TimeSpan.FromHours(2), configuring.IdleDeadline);
        Assert.All(updates.Where(u => u.State != "Configuring"), u => Assert.Null(u.IdleDeadline));
        var last = updates[^1];
        Assert.Equal((1, 2), (last.BaseVersionNumber, last.ResultVersionNumber));
        Assert.NotNull(last.EndedAt);
        Assert.Equal(updates.Length, updates.Select(u => (u.State, u.Message)).Distinct().Count()); // one push per state or message change
    }

    [Fact]
    public async Task APublisherThatThrows_DoesNotFailTheJob()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql, configure: s => s.AddSingleton<ITemplateJobPublisher>(new ThrowingPublisher()));
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);

        await rig.PumpAsync(rig.Start(id));

        Assert.Equal(TemplateJobState.Published, (await rig.JobAsync(id)).State);
    }

    private sealed class ThrowingPublisher : ITemplateJobPublisher
    {
        public Task PublishAsync(TemplateJobDto job, CancellationToken ct) => throw new InvalidOperationException("hub is down");
    }

    [Fact]
    public async Task Cancel_InConfiguring_DiscardsEverything()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit, NetworkMode.Direct);
        var run = rig.Start(id);
        await rig.PumpUntilAsync(id, TemplateJobState.Configuring, run);

        await rig.RequestCancelAsync(id);
        await rig.PumpAsync(run);

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Cancelled, job.State);
        Assert.NotNull(job.EndedAt);
        await rig.AssertDiscardedAsync(id);
        Assert.Empty(rig.Store.Sealed);
        var audits = await rig.AuditsAsync();
        // The cancel endpoint audits an admin's cancel (R17); the coordinator writes no second row for it.
        Assert.Equal(["template.egress.start", "template.egress.stop"], audits.Select(a => a.Action));
    }

    [Fact]
    public async Task Cancel_InSealing_DestroysTheVm_AndDeletesTheWorkingCopy()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);
        var connects = 0;
        // The first connection is the provenance check; once the sealer connects (the second), the guest stops answering it.
        rig.Channels.Silent = vm =>
        {
            if (vm.TaskId == id && Interlocked.Increment(ref connects) == 2) rig.Guest.SilentProvenance = true;
            return false;
        };
        var run = rig.Start(id);
        await rig.PumpUntilAsync(id, TemplateJobState.Sealing, run);
        await rig.IdleAsync(10);

        await rig.RequestCancelAsync(id);
        await rig.PumpAsync(run);

        Assert.Equal(TemplateJobState.Cancelled, (await rig.JobAsync(id)).State);
        await rig.AssertDiscardedAsync(id);
        Assert.Empty(rig.Store.Sealed);
        Assert.DoesNotContain(await rig.AuditsAsync(), a => a.Action == "template.job.cancel"); // the endpoint's row, not the coordinator's (R17)
    }

    [Fact]
    public async Task Cancel_InVerifying_DeletesTheSealedFiles_AndCreatesNoVersion()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);
        rig.Channels.Silent = vm => vm.TaskId != id; // the verification guest never says ready
        var run = rig.Start(id);
        await rig.PumpUntilAsync(id, TemplateJobState.Verifying, run);
        await rig.IdleAsync(10);

        await rig.RequestCancelAsync(id);
        await rig.PumpAsync(run);

        Assert.Equal(TemplateJobState.Cancelled, (await rig.JobAsync(id)).State);
        Assert.Equal([TemplateCoordinator.StemOf(id)], rig.Store.Sealed); // it had been sealed ...
        await rig.AssertDiscardedAsync(id);                               // ... and is deleted again
    }

    [Fact]
    public async Task IdleTimeout_DiscardsTheSession_CountingFromTheLastConsoleConnection()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit);
        var run = rig.Start(id);
        await rig.PumpUntilAsync(id, TemplateJobState.Configuring, run);
        var since = (await rig.JobAsync(id)).ConfiguringSince!.Value;

        // 1.5 h in, the admin (re)connects the console: the 2 h idle window starts again from there.
        rig.H.Time.Advance(since + TimeSpan.FromHours(1.5) - rig.H.Time.GetUtcNow());
        await rig.WriteJobAsync(id, j => j.LastConsoleAt = rig.H.Time.GetUtcNow());
        rig.H.Time.Advance(TimeSpan.FromHours(1));
        await rig.IdleAsync();
        Assert.Equal(TemplateJobState.Configuring, (await rig.JobAsync(id)).State); // 2.5 h after start, 1 h after the console

        rig.H.Time.Advance(TimeSpan.FromHours(1.1));
        await rig.PumpAsync(run);

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Cancelled, job.State);
        Assert.Equal("discarded after 2 h without a console connection", job.Message);
        await rig.AssertDiscardedAsync(id);
        var cancel = Assert.Single(await rig.AuditsAsync());
        Assert.Equal(("template.job.cancel", (Guid?)null), (cancel.Action, cancel.UserId)); // the system discarded it
    }

    [Fact]
    public async Task VerifyFailure_FailsTheJob_DeletesTheSealedFiles_AndCreatesNoVersion()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        rig.Guest.ProvenanceRealTimeProtection = true;
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("Defender real-time protection is on; turn it off in an Edit session", job.FailureReason);
        Assert.Equal([TemplateCoordinator.StemOf(id)], rig.Store.Sealed);
        await rig.AssertDiscardedAsync(id);
        var fail = Assert.Single(await rig.AuditsAsync());
        Assert.Equal(("template.job.fail", (Guid?)null), (fail.Action, fail.UserId));
        Assert.Contains(job.FailureReason!, fail.Details);
    }

    [Fact]
    public async Task SealFailure_FailsTheJob_DestroysTheVm_AndDeletesTheWorkingCopy()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        rig.Guest.RejectApplyComponents = true;
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.StartsWith("installing the components failed", job.FailureReason);
        Assert.Empty(rig.Store.Sealed);
        await rig.AssertDiscardedAsync(id);
    }

    [Fact]
    public async Task UnreadableDefenderState_DoesNotFail_AndIsNotedOnTheJob()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        rig.Guest.ProvenanceRealTimeProtection = null;
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Published, job.State);
        Assert.Contains(TemplateVerifier.DefenderUnreadableNote, job.Message);
    }

    [Theory]
    [InlineData(TemplateJobKind.Refresh)]
    [InlineData(TemplateJobKind.Edit)]
    public async Task AgentInstall_RunsOnTheJobStem_AfterTheWorkingCopyAndBeforeTheLeaseAndTheBoot(TemplateJobKind kind)
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var id = await rig.SeedJobAsync(kind);
        var stem = TemplateCoordinator.StemOf(id);
        (string State, string? Message, string[] Copies, int Vms, string[] Egress)? seen = null;
        byte[]? installedBytes = null;
        UnixFileMode installedMode = 0;
        rig.DiskEditor.OnInstall = async (_, _) =>
        {
            var file = rig.DiskEditor.Installs.Single().AgentExePath;
            installedBytes = await File.ReadAllBytesAsync(file, Ct);
            if (!OperatingSystem.IsWindows()) installedMode = File.GetUnixFileMode(file);
            var job = await rig.JobAsync(id);
            seen = (job.State.ToString(), job.Message, rig.Store.WorkingCopies.Select(c => c.To).ToArray(), (await rig.Hypervisor.ListAsync(Ct)).Count, [.. rig.Egress.Calls]);
        };
        var run = rig.Start(id);
        if (kind == TemplateJobKind.Edit)
        {
            await rig.PumpUntilAsync(id, TemplateJobState.Configuring, run);
            await rig.RequestSealAsync(id);
        }
        await rig.PumpAsync(run);

        var install = Assert.Single(rig.DiskEditor.Installs);
        Assert.Equal(stem, install.Stem);
        Assert.Equal(rig.AgentExe, installedBytes); // the bundle's bytes, from a private temp file
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, installedMode);
        Assert.False(File.Exists(install.AgentExePath)); // removed afterwards
        Assert.Equal(Path.Combine(rig.RunDir, "template-agent"), Path.GetDirectoryName(install.AgentExePath)); // under the run dir, not /tmp
        Assert.Equal(("Preparing", "installing the current agent into the working copy", new[] { stem }, 0, Array.Empty<string>()), seen);
        Assert.Equal(TemplateJobState.Published, (await rig.JobAsync(id)).State);
    }

    [Fact]
    public async Task AgentInstall_Failure_FailsTheJob_DeletesTheWorkingCopy_AndStartsNoVm()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        rig.DiskEditor.Failure = new TemplateAgentInstallException("writing the agent into the working copy failed (guestfish exit 1): mount failed");
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("writing the agent into the working copy failed (guestfish exit 1): mount failed", job.FailureReason);
        Assert.Null(job.ConfiguringSince);
        await rig.AssertDiscardedAsync(id);
        Assert.Empty(rig.Egress.Calls); // no lease was ever made
        Assert.Empty(rig.Store.Sealed);
    }

    [Fact]
    public async Task AgentInstall_ThatHangs_FailsAfterTheInstallTimeout()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Templates:Jobs:AgentInstallTimeout"] = "00:00:02" });
        rig.DiskEditor.OnInstall = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("writing the agent into the working copy did not finish within 2 s", job.FailureReason);
        await rig.AssertDiscardedAsync(id);
    }

    [Fact]
    public async Task AgentInstall_Cancel_DuringTheInstall_DiscardsEverything()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        rig.DiskEditor.OnInstall = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);
        var run = rig.Start(id);
        await rig.PumpUntilAsync(id, TemplateJobState.Preparing, run);
        await rig.IdleAsync(10);

        await rig.RequestCancelAsync(id);
        await rig.PumpAsync(run);

        Assert.Equal(TemplateJobState.Cancelled, (await rig.JobAsync(id)).State);
        await rig.AssertDiscardedAsync(id);
    }

    [Fact]
    public async Task TheRunNetDirectories_OfTheJobVmAndTheVerifyVm_AreDeletedAfterTheirLeases()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var leases = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        rig.Egress.BeforeCreate = leaseId =>
        {
            Directory.CreateDirectory(rig.NetDirOf(leaseId)); // what the router leaves: lease record, logs
            leases.Add(leaseId);
            return Task.CompletedTask;
        };
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);

        await rig.PumpAsync(rig.Start(id));

        Assert.Equal(TemplateJobState.Published, (await rig.JobAsync(id)).State);
        Assert.Equal(2, leases.Count); // the job's VM and the verification boot
        Assert.Contains(id, leases);
        Assert.All(leases, l => Assert.False(Directory.Exists(rig.NetDirOf(l)), $"run/net of lease {l} is still there"));
    }

    [Fact]
    public async Task ADirectSessionInterruptedWhileConfiguring_IsAuditedAsStopped_ByTheReconciler()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var open = await rig.SeedJobAsync(TemplateJobKind.Edit, NetworkMode.Direct, state: TemplateJobState.Configuring);
        var win11 = await rig.H.SeedTemplateAsync("win11");
        var closed = await rig.SeedJobAsync(TemplateJobKind.Edit, NetworkMode.Direct, state: TemplateJobState.Sealing, templateId: "win11", baseVersionId: win11.Id);
        await using (var db = rig.H.NewContext())
        {
            AuditLog Row(string action, string template, Guid job) => new()
            {
                At = rig.H.Time.GetUtcNow(), UserId = rig.AdminId, Action = action, Target = template, Details = TemplateCoordinator.DirectSessionDetails(job),
            };
            db.AuditLogs.AddRange(Row("template.egress.start", "win10", open), Row("template.egress.start", "win11", closed), Row("template.egress.stop", "win11", closed));
            await db.SaveChangesAsync(Ct);
        }

        await rig.H.Get<Reconciler>().ReconcileAsync(Ct);

        var stops = (await rig.AuditsAsync()).Where(a => a.Action == "template.egress.stop").ToList();
        Assert.Equal(2, stops.Count); // one new: the session that was still open; the closed one is not stopped twice
        var added = stops.Single(a => a.Details == TemplateCoordinator.DirectSessionDetails(open));
        Assert.Equal(((Guid?)null, "win10"), (added.UserId, added.Target));
        Assert.Equal(TemplateJobState.Failed, (await rig.JobAsync(open)).State);
    }

    [Fact]
    public async Task Reconciler_DeletesLeftoverAgentCopies()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var dir = Path.Combine(rig.RunDir, "template-agent");
        Directory.CreateDirectory(dir);
        var leftover = Path.Combine(dir, TemplateCoordinator.AgentCopyPrefix + Guid.NewGuid().ToString("N") + ".exe");
        var other = Path.Combine(dir, "keep.txt");
        await File.WriteAllBytesAsync(leftover, [1, 2, 3], Ct);
        await File.WriteAllBytesAsync(other, [1], Ct);

        await rig.H.Get<Reconciler>().ReconcileAsync(Ct);

        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(other)); // only the job's own copies
    }

    /// <summary>Throws from the commit of the transaction that recorded <see cref="VersionId"/>, after it has committed (the answer is lost).</summary>
    private sealed class LostCommitAnswer : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public Guid VersionId { get; set; }
        public int Thrown;

        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<TemplateVersion>().Any(e => e.Entity.Id == VersionId) == true && Interlocked.Increment(ref Thrown) == 1)
                throw new InvalidOperationException("the connection broke while the commit was answered");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AnErrorAfterTheVersionWasCommitted_KeepsItsFiles_AndTheJobAsRecorded()
    {
        var lost = new LostCommitAnswer();
        await using var rig = await TemplateJobRig.CreateAsync(sql, configure: s => s.ConfigureDbContext<MaldexDbContext>(o => o.AddInterceptors(lost)));
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);
        lost.VersionId = id;

        await rig.PumpAsync(rig.Start(id));

        Assert.Equal(1, lost.Thrown);
        var job = await rig.JobAsync(id);
        Assert.Equal((TemplateJobState.Published, (Guid?)id), (job.State, job.ResultVersionId));
        Assert.Equal(id, await rig.CurrentVersionIdAsync());
        Assert.DoesNotContain(TemplateCoordinator.StemOf(id), rig.Store.Deleted); // the recorded version's disk stays
        Assert.DoesNotContain(await rig.AuditsAsync(), a => a.Action == "template.job.fail");
    }

    /// <summary>Fails every save that would end a template job (the database refuses the terminal write).</summary>
    private sealed class FailingTerminalSave : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public int Failures;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TemplateJob>().Any(e => e.State == EntityState.Modified && Maldex.Core.Lifecycle.TemplateJobStateMachine.IsTerminal(e.Entity.State)))
            {
                Interlocked.Increment(ref Failures);
                throw new DbUpdateException("the database is unreachable");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task TerminalSaveRetries_StopAtHostShutdown_InsteadOfWaitingThemOut()
    {
        var failing = new FailingTerminalSave();
        await using var rig = await TemplateJobRig.CreateAsync(sql, configure: s => s.ConfigureDbContext<MaldexDbContext>(o => o.AddInterceptors(failing)));
        rig.Store.Missing.Add(rig.V1.FileStem); // the job fails at once
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);
        using var shutdown = new CancellationTokenSource();
        var run = Task.Run(() => rig.Coordinator.RunAsync(id, shutdown.Token), Ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref failing.Failures) == 0)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "The terminal save was never tried.");
            await Task.Delay(10, Ct);
        }

        // The fake clock stands still, so only the shutdown can end the 1 s retry wait.
        await shutdown.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(1, failing.Failures);
        Assert.Equal(TemplateJobState.Queued, (await rig.JobAsync(id)).State); // left for the reconciler
    }

    [Fact]
    public async Task WhileTheSlotGateIsHeld_NeitherTheAnalysisNorTheJobTakesTheSlot()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var analyses = rig.H.Get<OrchestratorWorker>();
        var gate = rig.H.Get<VmSlotGate>();
        var task = await rig.H.SeedTaskAsync(RunStatus.Queued);
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);
        var held = await gate.EnterAsync(Ct);
        Task<IReadOnlyList<Guid>>? claim = null;
        Task? run = null;
        try
        {
            claim = Task.Run(() => analyses.ClaimAndStartAsync(Ct), Ct);
            run = rig.Start(id);
            await rig.PumpUntilAsync(id, TemplateJobState.Preparing, run);
            await rig.IdleAsync();

            Assert.False(claim.IsCompleted); // the analysis claim waits at the gate
            Assert.Equal(RunStatus.Queued, await rig.H.StatusOfAsync(task));
            var job = await rig.JobAsync(id);
            Assert.Equal((TemplateJobState.Preparing, (DateTimeOffset?)null), (job.State, job.SlotHeldAt)); // so does the job
            Assert.Empty(await rig.Hypervisor.ListAsync(Ct));
        }
        finally
        {
            held.Dispose();
        }

        // Released: exactly one of them gets the one slot.
        var claimed = await claim!.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        try
        {
            if (claimed.Count == 0) await rig.PumpAsync(run!); // the job won; it runs to the end
            else
            {
                Assert.Equal([task], claimed);
                await rig.IdleAsync();
                Assert.Null((await rig.JobAsync(id)).SlotHeldAt); // the analysis won; the job waits
                await rig.RequestCancelAsync(id);
                await rig.PumpAsync(run!);
            }
            Assert.Equal(1, ((FakeHypervisor)rig.Hypervisor).MaxRunning);
        }
        finally
        {
            await analyses.StopRunsAsync();
        }
    }

    [Fact]
    public async Task MissingAgentExe_NeverTouchesTheDisk()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        File.Delete(rig.AgentExePath);
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);

        await rig.PumpAsync(rig.Start(id));

        Assert.Empty(rig.DiskEditor.Installs);
        Assert.Empty(rig.Store.WorkingCopies); // not even the copy
    }

    [Fact]
    public async Task AgentInstall_UsesTheBundlesBytes_EvenIfTheExeIsReplacedMeanwhile()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        byte[]? installed = null;
        rig.DiskEditor.OnInstall = async (_, _) => installed = await File.ReadAllBytesAsync(rig.DiskEditor.Installs.Single().AgentExePath, Ct);
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);
        rig.Store.OnCreateWorkingCopy = () => File.WriteAllBytes(rig.AgentExePath, [1, 2, 3]); // ./build.sh agent runs between load and install

        await rig.PumpAsync(rig.Start(id));

        Assert.Equal(rig.AgentExe, installed);
        Assert.Equal(TemplateJobState.Published, (await rig.JobAsync(id)).State);
    }

    [Fact]
    public async Task Sealer_SecondConnectionGetsNoReady_StillSealsAndPublishes() // R25
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);
        var connects = 0;
        rig.Channels.Silent = vm => vm.TaskId == id && Interlocked.Increment(ref connects) > 1; // no ready on the sealer's connection

        await rig.PumpAsync(rig.Start(id));

        Assert.Equal(TemplateJobState.Published, (await rig.JobAsync(id)).State);
    }

    [Fact]
    public async Task CheckAgent_ResendsProvenanceLostInTheGap_AndTheSealerToo()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        rig.Channels.DropProvenanceRequests = 2; // the check's and the sealer's connections lose their first two requests
        rig.Channels.DropOnConnections = 2;
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);

        await rig.PumpAsync(rig.Start(id));

        Assert.Equal(TemplateJobState.Published, (await rig.JobAsync(id)).State);
    }

    [Fact]
    public async Task CheckAgent_ConnectFailure_FailsTheJob()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit);
        rig.Channels.Unreachable = vm => vm.TaskId == id;

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("could not connect to the guest agent (the agent socket is not there); the agent could not be checked", job.FailureReason);
        await rig.AssertDiscardedAsync(id);
    }

    [Theory]
    [InlineData(TemplateJobKind.Refresh)]
    [InlineData(TemplateJobKind.Edit)]
    public async Task ProvenanceMismatch_AfterReady_FailsTheJob_BeforeConfiguringOrSealing(TemplateJobKind kind)
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        rig.Guest.ProvenanceAgentVersion = "0.9.0+aaaaaaaaaaaa";
        var id = await rig.SeedJobAsync(kind);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("the working copy still runs agent 0.9.0+aaaaaaaaaaaa; the agent could not be updated", job.FailureReason);
        Assert.Null(job.ConfiguringSince); // an edit never opened for the admin
        await rig.AssertDiscardedAsync(id);
        Assert.Empty(rig.Store.Sealed);
        Assert.Empty(rig.Guest.AppliedComponentIds); // nothing was installed into it
    }

    [Fact]
    public async Task ProvenanceTimeout_FromAnOldAgentThatNeverAnswers_FailsTheJob()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Templates:Jobs:ProvenanceTimeout"] = "00:00:02" });
        rig.Guest.SilentProvenance = true;
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("the working copy still runs agent 0.1.0-fake+000000000000; the agent could not be updated", job.FailureReason); // the version its ready message named
        await rig.AssertDiscardedAsync(id);
    }

    [Fact]
    public async Task ProvenanceRefused_AsAnUnknownCommand_FailsTheJob()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        rig.Guest.RejectProvenance = true;
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("the working copy still runs agent 0.1.0-fake+000000000000; the agent could not be updated", job.FailureReason);
        await rig.AssertDiscardedAsync(id);
    }

    [Fact]
    public async Task AgentNeverReady_BeforeTheCheck_FailsTheJob_WithoutSealing()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Templates:Jobs:AgentReadyTimeout"] = "00:00:02" });
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);
        rig.Channels.Silent = vm => vm.TaskId == id;

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("the guest agent was not ready within 2 s; the agent could not be checked", job.FailureReason);
        await rig.AssertDiscardedAsync(id);
    }

    [Fact]
    public async Task MissingBaseDisk_FailsBeforeAnyWork()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        rig.Store.Missing.Add(rig.V1.FileStem);
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("the base version's disk is missing", job.FailureReason);
        Assert.Null(job.StartedAt);
        Assert.Empty(rig.Store.WorkingCopies);
        Assert.Empty(rig.Egress.Calls);
    }

    [Fact]
    public async Task LowDisk_FailsBeforeAnyWork_NamingFreeAndRequiredSpace()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        rig.Disk.Free = 12 * 1024;
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("not enough disk space for a template copy: 12 GiB free, 30 GiB required", job.FailureReason);
        Assert.Empty(rig.Store.WorkingCopies);
        Assert.Contains(rig.Disk.Paths, p => p == Path.GetFullPath(".data/vm/base")); // the template disks' filesystem
    }

    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public async Task LowDisk_CountsTheBaseVersionsSize_OnTopOfTheMargin()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        rig.Disk.Free = 40 * 1024;
        rig.Sizes[rig.V1.FileStem] = 15 * GiB;
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal((TemplateJobState.Failed, "not enough disk space for a template copy: 40 GiB free, 45 GiB required"), (job.State, job.FailureReason));
        Assert.Empty(rig.Store.WorkingCopies);
    }

    [Fact]
    public async Task LowDisk_CountsTheCopiesOfOtherJobsStillPreparing()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var win11 = await rig.H.SeedTemplateAsync("win11");
        await rig.SeedJobAsync(TemplateJobKind.Refresh, state: TemplateJobState.Preparing, templateId: "win11", baseVersionId: win11.Id);
        rig.Disk.Free = 40 * 1024;
        rig.Sizes[rig.V1.FileStem] = 5 * GiB;
        rig.Sizes[win11.FileStem] = 10 * GiB;
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal((TemplateJobState.Failed, "not enough disk space for a template copy: 40 GiB free, 45 GiB required"), (job.State, job.FailureReason));
    }

    [Fact]
    public async Task DiskCheck_OtherJobsPastPreparing_AreNotCounted()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var win11 = await rig.H.SeedTemplateAsync("win11");
        await rig.SeedJobAsync(TemplateJobKind.Edit, state: TemplateJobState.Configuring, templateId: "win11", baseVersionId: win11.Id); // its copy is done
        rig.Disk.Free = 40 * 1024;
        rig.Sizes[rig.V1.FileStem] = 5 * GiB;
        rig.Sizes[win11.FileStem] = 10 * GiB;
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh); // the seeded other job holds no VM slot: only the disk is under test

        await rig.PumpAsync(rig.Start(id));

        Assert.Equal(TemplateJobState.Published, (await rig.JobAsync(id)).State);
    }

    [Fact]
    public async Task MissingAgentExe_FailsWithAClearReason_BeforeCopying()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        File.Delete(rig.AgentExePath);
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.StartsWith("the guest agent is missing", job.FailureReason);
        Assert.Empty(rig.Store.WorkingCopies);
        Assert.Empty(await rig.Hypervisor.ListAsync(Ct));
    }

    [Fact]
    public async Task InterruptedJob_IsFailedOnRestart_ItsVmAndFilesAreGone_AndItsSlotIsFree()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit, state: TemplateJobState.Configuring);
        var win11 = await rig.H.SeedTemplateAsync("win11");
        var waiting = await rig.SeedJobAsync(TemplateJobKind.Refresh, templateId: "win11", baseVersionId: win11.Id);
        // What a crash leaves behind: the job's VM and its lease.
        var lease = await rig.Egress.CreateAsync(id, NetworkMode.None, null, Ct);
        await rig.Hypervisor.StartAsync(new VmSpec(id, TemplateCoordinator.StemOf(id), 4096, 2, lease.NetworkId, Writable: true), Ct);

        await rig.H.Get<Reconciler>().ReconcileAsync(Ct);

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("interrupted", job.FailureReason);
        Assert.NotNull(job.EndedAt);
        await rig.AssertDiscardedAsync(id);
        Assert.Equal(TemplateJobState.Queued, (await rig.JobAsync(waiting)).State); // a queued job never started: it simply runs later
        var fail = Assert.Single(await rig.AuditsAsync());
        Assert.Equal(("template.job.fail", (Guid?)null, "win10"), (fail.Action, fail.UserId, fail.Target));
    }

    [Fact]
    public async Task JobVm_HoldsAVmSlot_SoAQueuedAnalysisWaits()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var analyses = rig.H.Get<OrchestratorWorker>();
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit);
        var run = rig.Start(id);
        await rig.PumpUntilAsync(id, TemplateJobState.Configuring, run);
        var task = await rig.H.SeedTaskAsync(RunStatus.Queued);
        try
        {
            Assert.Empty(await analyses.ClaimAndStartAsync(Ct));
            Assert.Equal(RunStatus.Queued, await rig.H.StatusOfAsync(task));

            await rig.RequestCancelAsync(id);
            await rig.PumpAsync(run);

            Assert.Equal([task], await analyses.ClaimAndStartAsync(Ct));
        }
        finally
        {
            await analyses.StopRunsAsync();
        }
    }

    [Fact]
    public async Task SlotTakenWhileCopying_TheJobWaitsInPreparing_WithoutAVm()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var analysis = await rig.H.SeedTaskAsync(RunStatus.Running); // e.g. claimed by the analysis worker while the disk was copied
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);
        var run = rig.Start(id);
        await rig.PumpUntilAsync(id, TemplateJobState.Preparing, run);
        await rig.IdleAsync();

        var job = await rig.JobAsync(id);
        Assert.Equal((TemplateJobState.Preparing, "waiting for a free VM slot"), (job.State, job.Message));
        Assert.Empty(await rig.Hypervisor.ListAsync(Ct));
        Assert.Empty(rig.Egress.Calls);

        await rig.EndAnalysisAsync(analysis);
        await rig.PumpAsync(run);
        Assert.Equal(TemplateJobState.Published, (await rig.JobAsync(id)).State);
    }

    [Fact]
    public async Task SlotFreedWhileTheJobWaits_TheJobTakesIt_AndNoAnalysisBootsAlongside()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var analyses = rig.H.Get<OrchestratorWorker>();
        var running = await rig.H.SeedTaskAsync(RunStatus.Running);
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Egress.BeforeCreate = async taskId =>
        {
            if (taskId != id) return;
            reached.TrySetResult();
            await go.Task;
        };
        var run = rig.Start(id);
        await rig.PumpUntilAsync(id, TemplateJobState.Preparing, run);
        await rig.IdleAsync(); // waiting for the analysis' slot
        var queued = await rig.H.SeedTaskAsync(RunStatus.Queued);
        List<Guid> claimed = [];
        try
        {
            await rig.EndAnalysisAsync(running);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!reached.Task.IsCompleted)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "The job never took the freed slot.");
                rig.H.Time.Advance(TimeSpan.FromMilliseconds(50));
                await Task.Delay(1, Ct);
            }

            // The job has taken the slot and is about to boot: the analysis must not get it.
            claimed.AddRange(await analyses.ClaimAndStartAsync(Ct));
            Assert.Empty(claimed);

            go.SetResult();
            for (var i = 0; !run.IsCompleted; i++)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), "The job did not finish.");
                if (i % 20 == 0) claimed.AddRange(await analyses.ClaimAndStartAsync(Ct));
                rig.H.Time.Advance(TimeSpan.FromMilliseconds(50));
                await Task.Delay(1, Ct);
            }
            await run;
            Assert.Equal(TemplateJobState.Published, (await rig.JobAsync(id)).State);

            claimed.AddRange(await analyses.ClaimAndStartAsync(Ct));
            Assert.Equal([queued], claimed);
            while ((await rig.Hypervisor.ListAsync(Ct)).Count == 0)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(90), "The analysis never booted.");
                await rig.IdleAsync(5);
            }
            Assert.Equal(1, ((FakeHypervisor)rig.Hypervisor).MaxRunning); // never two VMs at once
        }
        finally
        {
            go.TrySetResult();
            await analyses.StopRunsAsync();
        }
    }

    [Fact]
    public async Task RacedJobsForOneTemplate_TheOlderRuns_AndTheNewerIsRefused()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        await using (var db = rig.H.NewContext())
        {
            // The one-active-job index normally stops this race at the endpoint; drop it to exercise the coordinator's own check.
            await db.Database.ExecuteSqlRawAsync("DROP INDEX IX_TemplateJobs_TemplateId_Active ON TemplateJobs", Ct);
        }
        var older = await rig.SeedJobAsync(TemplateJobKind.Edit);
        rig.H.Time.Advance(TimeSpan.FromSeconds(1));
        var newer = await rig.SeedJobAsync(TemplateJobKind.Refresh);

        var run = rig.Start(older); // a newer queued job does not block it
        await rig.PumpUntilAsync(older, TemplateJobState.Configuring, run);
        await rig.PumpAsync(rig.Start(newer));

        var refused = await rig.JobAsync(newer);
        Assert.Equal((TemplateJobState.Failed, TemplateCoordinator.AlreadyRunning), (refused.State, refused.FailureReason));
        await rig.RequestCancelAsync(older);
        await rig.PumpAsync(run);
        Assert.Equal(TemplateJobState.Cancelled, (await rig.JobAsync(older)).State);
    }

    [Fact]
    public async Task VmStartFailure_FailsTheJob_ReleasesTheLease_AndDeletesTheWorkingCopy()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Fake:Hypervisor:FailStarts"] = "1" });
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit);

        await rig.PumpAsync(rig.Start(id));

        var job = await rig.JobAsync(id);
        Assert.Equal(TemplateJobState.Failed, job.State);
        Assert.Equal("the VM could not be started: Fake hypervisor configured to fail this start.", job.FailureReason);
        Assert.Equal(["create None", "release"], rig.Egress.Calls);
        await rig.AssertDiscardedAsync(id);
    }

    [Fact]
    public async Task Worker_EndsAQueuedJobWithACancelRequest_EvenWithoutAFreeSlot()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var worker = rig.H.Get<TemplateJobWorker>();
        await rig.H.SeedTaskAsync(RunStatus.Running);
        var id = await rig.SeedJobAsync(TemplateJobKind.Edit);
        await rig.RequestCancelAsync(id);
        try
        {
            Assert.Equal([id], await worker.ClaimAndStartAsync(Ct));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((await rig.JobAsync(id)).State != TemplateJobState.Cancelled)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "The job was not cancelled.");
                await rig.IdleAsync(5);
            }
            Assert.Empty(rig.Store.WorkingCopies);
            Assert.Null((await rig.JobAsync(id)).StartedAt);
        }
        finally
        {
            await worker.StopRunsAsync();
        }
    }

    [Fact]
    public async Task Worker_StartsAQueuedJob_OnlyWhenAVmSlotIsFree()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var worker = rig.H.Get<TemplateJobWorker>();
        var analysis = await rig.H.SeedTaskAsync(RunStatus.Running);
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);
        try
        {
            Assert.Empty(await worker.ClaimAndStartAsync(Ct)); // the analysis holds the only slot
            Assert.Equal(TemplateJobState.Queued, (await rig.JobAsync(id)).State);

            await rig.EndAnalysisAsync(analysis);

            Assert.Equal([id], await worker.ClaimAndStartAsync(Ct));
            Assert.Empty(await worker.ClaimAndStartAsync(Ct)); // already running: never started twice
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((await rig.JobAsync(id)).State != TemplateJobState.Published)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), "The job did not finish.");
                await rig.IdleAsync(5);
            }
        }
        finally
        {
            await worker.StopRunsAsync();
        }
    }
}

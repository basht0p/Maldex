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

/// <summary>The fake egress router, recording every call (and the job's state at the moment a lease is isolated).</summary>
internal sealed class RecordingEgress(IEgressRouter inner) : IEgressRouter
{
    private readonly List<string> _calls = [];
    public IReadOnlyList<string> Calls { get { lock (_calls) return _calls.ToArray(); } }
    public Func<Guid, Task<string>>? DescribeAtIsolate { get; set; }

    private void Add(string call) { lock (_calls) _calls.Add(call); }

    public async Task<NetworkLease> CreateAsync(Guid taskId, NetworkMode mode, string? exitCountry, CancellationToken ct)
    {
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

    public Task<IAgentChannel> ConnectAsync(VmHandle vm, CancellationToken ct) =>
        Silent?.Invoke(vm) == true
            ? Task.FromResult<IAgentChannel>(new ReplayAgentChannel([], 1, holdOpen: true, time, guest, vm.TaskId))
            : inner.ConnectAsync(vm, ct);
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
    public FakeTemplateStore Store => (FakeTemplateStore)H.Get<ITemplateStore>();
    public IHypervisor Hypervisor => H.Get<IHypervisor>();
    public RecordingEgress Egress => (RecordingEgress)H.Get<IEgressRouter>();
    public SilenceableChannels Channels => (SilenceableChannels)H.Get<IAgentChannelFactory>();
    public SettableDiskSpace Disk => (SettableDiskSpace)H.Get<IDiskSpaceProbe>();
    public TemplateCoordinator Coordinator => H.Get<TemplateCoordinator>();

    public static async Task<TemplateJobRig> CreateAsync(SqlServerFixture sql, IDictionary<string, string?>? extra = null)
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
            services.Replace(ServiceDescriptor.Singleton<IDiskSpaceProbe>(new SettableDiskSpace()));
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
        Assert.Equal(["template.egress.start", "template.egress.stop", "template.job.cancel"], audits.Select(a => a.Action));
        Assert.Equal(rig.AdminId, audits[^1].UserId);
    }

    [Fact]
    public async Task Cancel_InSealing_DestroysTheVm_AndDeletesTheWorkingCopy()
    {
        await using var rig = await TemplateJobRig.CreateAsync(sql);
        var id = await rig.SeedJobAsync(TemplateJobKind.Refresh);
        rig.Channels.Silent = vm => vm.TaskId == id; // the job's guest never says ready, so the sealer keeps waiting
        var run = rig.Start(id);
        await rig.PumpUntilAsync(id, TemplateJobState.Sealing, run);
        await rig.IdleAsync(10);

        await rig.RequestCancelAsync(id);
        await rig.PumpAsync(run);

        Assert.Equal(TemplateJobState.Cancelled, (await rig.JobAsync(id)).State);
        await rig.AssertDiscardedAsync(id);
        Assert.Empty(rig.Store.Sealed);
        Assert.Equal(["template.job.cancel"], (await rig.AuditsAsync()).Select(a => a.Action));
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

        await using (var db = rig.H.NewContext())
        {
            var t = await db.Tasks.SingleAsync(x => x.Id == analysis, Ct);
            t.TransitionTo(RunStatus.Collecting, "test", rig.H.Time.GetUtcNow());
            t.TransitionTo(RunStatus.Processing, "test", rig.H.Time.GetUtcNow());
            await db.SaveChangesAsync(Ct);
        }
        await rig.PumpAsync(run);
        Assert.Equal(TemplateJobState.Published, (await rig.JobAsync(id)).State);
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

            await using (var db = rig.H.NewContext())
            {
                var t = await db.Tasks.SingleAsync(x => x.Id == analysis, Ct);
                t.TransitionTo(RunStatus.Collecting, "test", rig.H.Time.GetUtcNow());
                t.TransitionTo(RunStatus.Processing, "test", rig.H.Time.GetUtcNow());
                await db.SaveChangesAsync(Ct);
            }

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

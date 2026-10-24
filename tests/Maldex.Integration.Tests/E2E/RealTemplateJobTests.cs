using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Infrastructure.Isolation;
using Maldex.Infrastructure.Net;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Orchestrator;
using Maldex.Orchestrator.Templates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.E2E;

/// <summary>
/// Gated real-VM acceptance of template jobs on the hand-prepared <c>win11-26h2</c> image, driven through the admin HTTP API:
/// Refresh (seal, verify, publish, then the harmless E2E scenarios on the new version), Edit with Direct egress (the guest
/// reaches the internet and not the LAN, the host or the control plane; discard leaves nothing) and Rollback.
/// The base image is irreplaceable: nothing here writes, renames, chmods or deletes <c>win11-26h2.*</c>. Every disk a job
/// creates is <c>base/&lt;job id:N&gt;.*</c> and is deleted by the test's own cleanup (the test database is thrown away, so the
/// production sweep never sees those rows); each test ends by asserting that the base directory holds exactly the files it
/// held before. One collection with the other real-VM tests: one VM at a time. See deploy/guest/README.md "Running the E2E suite".
/// </summary>
[Collection("sql")]
public sealed partial class RealTemplateJobTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const string TemplateId = "win11-26h2";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(40);
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The checkout that holds the real <c>.data</c>: the repository root, or, when <c>.data</c> there is a symlink (a git
    /// worktree sharing the main checkout's image), the directory it points into. The host runs with this as
    /// <c>Maldex:Real:Root</c>, which also keeps the agent socket path within the 107-byte unix-socket limit.
    /// </summary>
    private static string Root
    {
        get
        {
            var data = new DirectoryInfo(Path.Combine(RealVmE2ETests.Root, ".data"));
            return data.LinkTarget is null ? RealVmE2ETests.Root : Path.GetDirectoryName(data.ResolveLinkTarget(returnFinalTarget: true)!.FullName)!;
        }
    }
    private static string RunNetDir => Path.Combine(Root, ".data", "vm", "run", "net");
    private static string BaseDir => Path.Combine(Root, ".data", "vm", "base");
    private static string AgentDir => Path.Combine(Root, ".data", "agent");

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex JobStem();

    private static void RequireRealStack()
    {
        RealVmE2ETests.RequireRealStack();
        var missing = new[] { "Maldex.Agent.exe", "Maldex.Agent.version" }.Where(f => !File.Exists(Path.Combine(AgentDir, f))).ToList();
        Assert.SkipWhen(missing.Count > 0,
            $"Real template-job E2E skipped: the guest agent build is missing in {AgentDir} ({string.Join(", ", missing)}). Run ./build.sh agent, then ./build.sh e2e.");
    }

    // ---- tests --------------------------------------------------------------------------------------------------

    [Fact(Timeout = 120 * 60 * 1000)]
    public async Task Refresh_SealsVerifiesAndPublishes_ThenAnalysesStillPass()
    {
        RequireRealStack();
        await WithHostAsync(TestContext.Current.CancellationToken, async (f, jobs) =>
        {
            using var admin = f.CreateClient(UserRole.Admin, "admin");
            var v1 = Current(await TemplateAsync(admin));
            Assert.Equal(1, v1.GetProperty("number").GetInt32());

            var job = await RefreshToPublishedAsync(f, admin, jobs);
            var v2 = Current(await TemplateAsync(admin));
            Assert.Equal(2, v2.GetProperty("number").GetInt32());
            Assert.Equal(job, v2.GetProperty("id").GetGuid()); // the version id is the job id (R15)
            Assert.Equal("Refresh", v2.GetProperty("origin").GetString());

            // The version carries the bundle's agent, and verification read Defender real-time protection as off (on fails the
            // job; unreadable publishes with a note in the job message).
            var bundle = await ComponentBundle.LoadAsync(f.Services.GetRequiredService<IOptions<TemplateJobOptions>>().Value, Ct);
            Assert.Equal(bundle.AgentBuildId, v2.GetProperty("agentVersion").GetString());
            var done = await JobAsync(admin, job);
            Assert.DoesNotContain(TemplateVerifier.DefenderUnreadableNote, done.GetProperty("message").GetString() ?? "", StringComparison.Ordinal);
            output.WriteLine($"v2: {v2.GetProperty("windowsEdition").GetString()} {v2.GetProperty("windowsBuild").GetString()}, agent {bundle.AgentBuildId}");

            // The three harmless scenarios on v2: each run binds to it at Preparing.
            var e2e = new RealVmE2ETests(sql, output);
            foreach (var (name, run) in new (string, Func<Task<Guid>>)[]
                     { ("EICAR", () => e2e.EicarAsync(f)), ("regwrite", () => e2e.RegWriteAsync(f)), ("URL", () => e2e.ControlledUrlAsync(f)) })
            {
                Progress($"analysis {name} on v2 ...");
                var taskId = await run();
                await using var db = Db(f);
                Assert.Equal(job, await db.Tasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => t.TemplateVersionId).SingleAsync(Ct));
                Progress($"analysis {name} on v2 passed");
            }
        });
    }

    [Fact(Timeout = 45 * 60 * 1000)]
    public async Task Edit_Direct_ReachesInternet_NotLan()
    {
        RequireRealStack();
        await WithHostAsync(TestContext.Current.CancellationToken, async (f, jobs) =>
        {
            using var admin = f.CreateClient(UserRole.Admin, "admin");
            var before = await TemplateAsync(admin);
            var v1 = Current(before);

            var job = await StartAsync(admin, "edit", new { network = "Direct" });
            jobs.Add(job);
            var configuring = await WaitForAsync(admin, job, s => s is "Configuring" || IsTerminal(s), JobTimeout);
            Assert.True(configuring.GetProperty("state").GetString() == "Configuring", $"edit job ended {Describe(configuring)}");
            Assert.Equal("Direct", configuring.GetProperty("network").GetString());

            // From the guest: the agent's self-test, with the Direct probe plan plus the LAN gateway and the host's uplink address.
            var verdict = await DirectSelfTestAsync(f, job);
            Assert.True(verdict.Isolated, $"Direct self-test failed: {verdict.Reason}");
            Progress("Direct self-test passed: public address reachable; LAN gateway, host and control plane unreachable");

            // Discard: Cancelled, no new version, the working copy gone, the base untouched.
            var cancel = await admin.PostAsync($"/api/admin/template-jobs/{job}/cancel", null, Ct);
            Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
            var ended = await WaitForAsync(admin, job, IsTerminal, TimeSpan.FromMinutes(10));
            Assert.Equal("Cancelled", ended.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, ended.GetProperty("resultVersionNumber").ValueKind);

            var after = await TemplateAsync(admin);
            Assert.Equal(1, after.GetProperty("versions").GetArrayLength());
            Assert.Equal(v1.GetProperty("id").GetGuid(), Current(after).GetProperty("id").GetGuid());
            Assert.Empty(JobFiles(job));
            await using var db = Db(f);
            Assert.Contains(await db.AuditLogs.AsNoTracking().Where(a => a.Target == TemplateId).Select(a => a.Action).ToListAsync(Ct),
                a => a == "template.egress.stop");
        });
    }

    [Fact(Timeout = 75 * 60 * 1000)]
    public async Task Rollback_AfterRefresh_RetiresV2()
    {
        RequireRealStack();
        await WithHostAsync(TestContext.Current.CancellationToken, async (f, jobs) =>
        {
            using var admin = f.CreateClient(UserRole.Admin, "admin");
            var v1 = Current(await TemplateAsync(admin)).GetProperty("id").GetGuid();
            var v2 = await RefreshToPublishedAsync(f, admin, jobs); // this test's own refresh: it does not depend on test order

            var resp = await admin.PostAsync($"/api/admin/templates/{TemplateId}/rollback", null, Ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var template = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal(v1, Current(template).GetProperty("id").GetGuid());
            var rolledBackFrom = template.GetProperty("versions").EnumerateArray().Single(v => v.GetProperty("id").GetGuid() == v2);
            Assert.Equal("Retired", rolledBackFrom.GetProperty("status").GetString());
            Progress("rolled back: v1 current, v2 Retired");
        });
    }

    // ---- job driving --------------------------------------------------------------------------------------------

    /// <summary>Refreshes the current version and waits for Published; returns the job id (= the new version's id).</summary>
    private async Task<Guid> RefreshToPublishedAsync(MaldexAppFactory f, HttpClient admin, List<Guid> jobs)
    {
        var job = await StartAsync(admin, "refresh", new { });
        jobs.Add(job);
        var ended = await WaitForAsync(admin, job, IsTerminal, JobTimeout);
        Assert.True(ended.GetProperty("state").GetString() == "Published", $"refresh job ended {Describe(ended)}");
        Assert.Equal(2, ended.GetProperty("resultVersionNumber").GetInt32());
        Assert.Equal(2, JobFiles(job).Count); // the sealed version: <stem>.qcow2 and <stem>.vars.fd
        return job;
    }

    private static async Task<Guid> StartAsync(HttpClient admin, string kind, object body)
    {
        var resp = await admin.PostAsJsonAsync($"/api/admin/templates/{TemplateId}/{kind}", body, Ct);
        Assert.True(resp.StatusCode == HttpStatusCode.Accepted, $"{kind}: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> JobAsync(HttpClient admin, Guid job) =>
        await admin.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{job}", Ct);

    /// <summary>Polls the job until <paramref name="done"/> holds for its state; prints every change of state or message.</summary>
    private async Task<JsonElement> WaitForAsync(HttpClient admin, Guid job, Func<string, bool> done, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        string? last = null;
        while (true)
        {
            var dto = await JobAsync(admin, job);
            var state = dto.GetProperty("state").GetString()!;
            var line = $"{state}: {dto.GetProperty("message").GetString()}";
            if (line != last) Progress($"job {job:N} {line}");
            last = line;
            if (done(state)) return dto;
            Assert.True(DateTimeOffset.UtcNow < deadline, $"job {job} still {line} after {timeout}");
            await Task.Delay(PollEvery, Ct);
        }
    }

    private static bool IsTerminal(string state) => state is "Published" or "Sealed" or "Failed" or "Cancelled";

    private static string Describe(JsonElement job) =>
        $"{job.GetProperty("state").GetString()} (message: {job.GetProperty("message").GetString()}; failure: {job.GetProperty("failureReason").GetString()})";

    private static async Task<JsonElement> TemplateAsync(HttpClient admin) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/admin/templates", Ct)).EnumerateArray().Single(t => t.GetProperty("id").GetString() == TemplateId);

    private static JsonElement Current(JsonElement template) => template.GetProperty("current");

    // ---- the Direct probe ---------------------------------------------------------------------------------------

    /// <summary>
    /// Connects to the edit guest's agent (the coordinator holds no channel while Configuring), waits for its ready message and
    /// runs the self-test gate in Direct mode: the run's probe plan (gateway, control plane, private ranges, link-local, and the
    /// Direct probe URL as the echo), plus the LAN's default gateway and the host's uplink address.
    /// </summary>
    private async Task<IsolationVerdict> DirectSelfTestAsync(MaldexAppFactory f, Guid job)
    {
        var lanGateway = (await RealVmE2ETests.RunToolAsync(Ct, "sh", "-c", "ip -4 -o route show default | awk '{print $3; exit}'")).Trim();
        var hostIp = (await RealVmE2ETests.RunToolAsync(Ct, "sh", "-c", "ip -4 -o route get 1.1.1.1 | awk '{for (i=1;i<NF;i++) if ($i==\"src\") print $(i+1); exit}'")).Trim();
        Assert.False(string.IsNullOrEmpty(lanGateway) || string.IsNullOrEmpty(hostIp), "no LAN default gateway / host uplink address found");

        var info = await f.Services.GetRequiredService<IRunNetworkInfo>().GetAsync(new NetworkLease(job, RunNetNames.For(job).Tap), Ct);
        Assert.NotNull(info);
        Assert.Equal(NetworkMode.Direct, info.Mode);
        var net = f.Services.GetRequiredService<IOptions<NetOptions>>().Value;
        var plan = IsolationProbePlan.Build(net, info);
        Assert.Contains(plan.Targets, t => t.Name.StartsWith("control_plane_", StringComparison.Ordinal));
        // No port 53: every guest DNS packet, whatever its address, is redirected to the run's own dnsmasq, so a connect to
        // <any>:53 is answered by the gateway and proves nothing about the LAN.
        plan = plan with
        {
            Targets = [.. plan.Targets, new ProbeTarget("lan_gateway", lanGateway, [22, 80, 443, 8080]), new ProbeTarget("host_uplink", hostIp, [22, 80, 443, 445, 3389, 5000, 8080])],
        };
        Progress($"Direct self-test: echo {plan.EchoUrl}; must not reach {string.Join(", ", plan.Targets.Select(t => $"{t.Name}={t.Host}"))}");

        await using var channel = await f.Services.GetRequiredService<IAgentChannelFactory>().ConnectAsync(VmHandle.For(job), Ct);
        var inbox = new TransferInbox();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopPump = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pump = Task.Run(async () =>
        {
            Exception? error = null;
            try
            {
                await foreach (var m in channel.ReadAsync(stopPump.Token))
                {
                    if (m is ReadyMessage) ready.TrySetResult();
                    else await inbox.OfferAsync(m, stopPump.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { error = ex; }
            finally
            {
                inbox.Complete(error);
                ready.TrySetException(new InvalidOperationException("the agent disconnected before it was ready", error));
            }
        });
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromMinutes(5), Ct);
            Progress("edit guest agent ready");
            return await new SelfTestGate(TimeProvider.System, timeout: TimeSpan.FromMinutes(2)).RunAsync(channel, inbox, NetworkMode.Direct, null, plan, Ct);
        }
        finally
        {
            await stopPump.CancelAsync();
            await pump;
        }
    }

    // ---- host, cleanup and the base-image guard -----------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="body"/> on a real-mode host with workers. Afterwards, whatever happened: active jobs are cancelled
    /// (bounded), the host is disposed, VMs and network resources of the jobs are force-removed, the jobs' disk files are
    /// deleted, and the base directory must hold exactly the files it held before, with the base image unchanged.
    /// </summary>
    private async Task WithHostAsync(CancellationToken ct, Func<MaldexAppFactory, List<Guid>, Task> body)
    {
        var before = Snapshot();
        var domainsBefore = await DomainsAsync(CancellationToken.None);
        var runNetBefore = RunNetDirs();
        var jobs = new List<Guid>();
        var roundTrips = new ProvenanceRoundTrips();
        var f = new RecordingHost(sql, roundTrips, new Dictionary<string, string?>
        {
            ["Maldex:Mode"] = "Real",
            ["Maldex:Workers:Enabled"] = "true",
            ["Maldex:Real:Root"] = Root,
            // The job's component paths are taken as given (relative to the process directory), so pin them to the repository.
            ["Maldex:Templates:Jobs:AgentExePath"] = Path.Combine(AgentDir, "Maldex.Agent.exe"),
            ["Maldex:Templates:Jobs:AgentVersionPath"] = Path.Combine(AgentDir, "Maldex.Agent.version"),
            ["Maldex:Templates:Jobs:SysmonConfigPath"] = Path.Combine(Root, "deploy", "guest", "provision", "sysmon-config.xml"),
            ["Maldex:Templates:Jobs:MitmCaPemPath"] = Path.Combine(Root, ".data", "mitm", "ca", "mitmproxy-ca-cert.pem"),
            // Never let retention retire (and then delete) the imported v1, whose files are the hand-prepared image.
            ["Maldex:Templates:KeepPrevious"] = "10",
        });
        Exception? failure = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            await body(f, jobs);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        foreach (var line in roundTrips.Describe(vm => jobs.Any(j => vm == VmHandle.For(j).Name) ? $"job {vm[7..15]}" : $"verification boot {vm[7..15]}"))
            Progress("provenance: " + line);

        var problems = new List<string>();
        foreach (var job in jobs)
        {
            try { await CancelIfActiveAsync(f, job); }
            catch (Exception ex) { problems.Add($"cancelling job {job:N} failed: {ex.Message}"); }
        }
        try { f.Dispose(); }
        catch (Exception ex) { problems.Add($"disposing the host failed: {ex.Message}"); }

        using (var bound = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            try
            {
                foreach (var job in jobs)
                {
                    var left = await RealVmE2ETests.FindAsync(job, bound.Token);
                    if (left.IsEmpty) continue;
                    problems.Add($"job {job:N} left behind: {left} (force-removed)");
                    await RealVmE2ETests.ForceRemoveAsync(left, bound.Token);
                }
                // Verification VMs have ids of their own: any new maldex-* domain is a leak.
                foreach (var d in (await DomainsAsync(bound.Token)).Except(domainsBefore))
                {
                    problems.Add($"domain {d} left behind (destroyed)");
                    await RealVmE2ETests.RunToolAsync(bound.Token, "virsh", "-c", "qemu:///session", "destroy", d);
                    await RealVmE2ETests.RunToolAsync(bound.Token, "virsh", "-c", "qemu:///session", "undefine", "--nvram", d);
                }
            }
            catch (Exception ex) { problems.Add($"leak check failed: {ex.Message}"); }

            // The router keeps a run's net directory (lease.json, flows) for collection; no one collects a template job's or
            // a verification boot's. They belong to this test's runs only (created after the snapshot): remove them.
            foreach (var dir in RunNetDirs().Except(runNetBefore))
            {
                Progress($"removing the run net directory {dir} left by this test's runs");
                try { await RealVmE2ETests.RunToolAsync(bound.Token, "sudo", "-n", "rm", "-rf", "--", Path.Combine(RunNetDir, dir)); }
                catch (Exception ex) { problems.Add($"removing {dir} failed: {ex.Message}"); }
            }
        }

        foreach (var job in jobs)
        {
            foreach (var file in JobFiles(job))
            {
                try { File.Delete(file); }
                catch (Exception ex) { problems.Add($"deleting {file} failed: {ex.Message}"); }
            }
        }

        var after = Snapshot();
        if (!after.SequenceEqual(before))
            problems.Add($"the base directory changed: before [{string.Join(", ", before)}], after [{string.Join(", ", after)}]");

        var leak = problems.Count == 0 ? null : string.Join("; ", problems);
        if (failure is not null && leak is not null) throw new AggregateException($"template E2E failed AND cleanup reported problems: {leak}", failure, new InvalidOperationException(leak));
        if (leak is not null) throw new InvalidOperationException(leak);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Asks a still-active job to cancel and waits up to 5 min for a terminal state.</summary>
    private static async Task CancelIfActiveAsync(MaldexAppFactory f, Guid job)
    {
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        for (var i = 0; ; i++)
        {
            var dto = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{job}", bound.Token);
            if (IsTerminal(dto.GetProperty("state").GetString()!)) return;
            if (i % 6 == 0) await admin.PostAsync($"/api/admin/template-jobs/{job}/cancel", null, bound.Token);
            await Task.Delay(TimeSpan.FromSeconds(5), bound.Token);
        }
    }

    /// <summary>The disk files of a job's stem (working copy, sealed version, partial copy). Only a 32-hex stem, never the base image.</summary>
    private static List<string> JobFiles(Guid job)
    {
        var stem = TemplateCoordinator.StemOf(job);
        Assert.Matches(JobStem(), stem);
        Assert.NotEqual(TemplateId, stem);
        return new[] { ".qcow2", ".vars.fd", ".qcow2.tmp", ".vars.fd.tmp" }
            .Select(ext => Path.Combine(BaseDir, stem + ext)).Where(File.Exists).ToList();
    }

    /// <summary>Every file of the base directory with its size, mode and write time (so a change to the base image shows too).</summary>
    private static List<string> Snapshot() =>
        new DirectoryInfo(BaseDir).EnumerateFiles().OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => $"{f.Name} {f.Length} {f.UnixFileMode} {f.LastWriteTimeUtc:O}").ToList();

    [GeneratedRegex("^mxn[0-9a-f]{12}$")]
    private static partial Regex RunNamespace();

    /// <summary>The per-run net directories (<c>mxn</c> + 12 hex only).</summary>
    private static List<string> RunNetDirs() =>
        Directory.Exists(RunNetDir)
            ? Directory.EnumerateDirectories(RunNetDir).Select(Path.GetFileName).OfType<string>().Where(n => RunNamespace().IsMatch(n)).ToList()
            : [];

    private static async Task<List<string>> DomainsAsync(CancellationToken ct) =>
        RealVmE2ETests.Lines(await RealVmE2ETests.RunToolAsync(ct, "virsh", "-c", "qemu:///session", "list", "--all", "--name"))
            .Where(d => d.StartsWith("maldex-", StringComparison.Ordinal)).ToList();

    private static MaldexDbContext Db(MaldexAppFactory f) =>
        new(new DbContextOptionsBuilder<MaldexDbContext>().UseSqlServer(f.ConnectionString).Options);

    private void Progress(string line)
    {
        var text = $"{DateTimeOffset.UtcNow:HH:mm:ss} {line}";
        output.WriteLine(text);
        Console.WriteLine(text);
    }

    /// <summary>The real-mode host with its agent channel factory wrapped by <see cref="ProvenanceRoundTrips"/>.</summary>
    private sealed class RecordingHost(SqlServerFixture sql, ProvenanceRoundTrips roundTrips, IReadOnlyDictionary<string, string?> settings)
        : MaldexAppFactory(sql, workers: true, settings: settings)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var real = services.Last(d => d.ServiceType == typeof(IAgentChannelFactory));
                services.Remove(real);
                services.AddSingleton(real.ImplementationType!);
                services.AddSingleton<IAgentChannelFactory>(sp => roundTrips.Wrap((IAgentChannelFactory)sp.GetRequiredService(real.ImplementationType!)));
            });
        }
    }
}

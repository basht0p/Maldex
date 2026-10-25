using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Orchestrator.Templates;
using Maldex.Orchestrator.Templates.Build;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.E2E;

/// <summary>
/// Gated real-VM acceptance of a Build from installer media on this host, driven through the admin HTTP API: the ISO named by
/// <c>MALDEX_WIN_ISO</c> is fetched into a media library of the test's own from a loopback static server (real fetcher, real
/// read-only loop-mount inspection), Windows is installed unattended and provisioned, the post-reboot agent check passes, a Seal
/// is refused while Defender real-time protection is on, and the discard leaves nothing behind. Run by <c>./build.sh e2e-templates</c>.
/// The hand-prepared <c>win11-26h2</c> base image and the ISO are never written; both are checked unchanged afterwards.
/// </summary>
[Collection("sql")]
public sealed class RealTemplateBuildTests(SqlServerFixture sql, ITestOutputHelper output)
{
    // Its own waits: 60 (fetch) + 210 (Configuring) + 10 (seal refusal) + 10 (cancel) min, plus the cleanup.
    [Fact(Timeout = 300 * 60 * 1000)]
    public async Task Build_FromIso_ReachesConfiguring_ThenDiscards()
    {
        RealBuildHarness.RequireRealStack();
        await new RealBuildHarness(sql, output).RunAsync(async run =>
        {
            var media = await run.FetchIsoAsync();
            var templateId = "e2e-build-" + Guid.NewGuid().ToString("N")[..8];
            var job = await run.StartBuildAsync(templateId, "E2E build", media, publish: false);

            var configuring = await run.WaitForConfiguringAsync(job);
            // Provisioning reported real-time protection on (Pro media: Tamper Protection), so the admin is told to turn it off.
            Assert.Equal(TemplateCoordinator.DefenderGuidance, configuring.GetProperty("message").GetString());
            Assert.Equal("None", configuring.GetProperty("network").GetString());

            // The coordinator's post-reboot agent check passed (the job reached Configuring) on a connection of its own: the
            // installer's and the check's connections to the job's VM both had their provenance answered.
            var answered = run.RoundTrips.Connections.Count(c => c.Vm == VmHandle.For(job).Name && c.AnsweredAfter is not null);
            Assert.True(answered >= 2, $"expected the install and the post-reboot check to get provenance answers, got {answered}");
            // And the guest runs the bundle's agent, on the Pro edition, with real-time protection on.
            var bundle = await run.BundleAsync();
            // Right after the boot Defender may still be starting and report real-time protection off (seen on this host): read
            // until it settles on, bounded, so the seal below meets the state provisioning reported.
            var p = await run.ProvenanceAsync(job);
            for (var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5); p.RealTimeProtectionEnabled != true && DateTimeOffset.UtcNow < deadline;)
            {
                run.Progress($"real-time protection reads {p.RealTimeProtectionEnabled?.ToString() ?? "unknown"}; reading again in 15 s");
                await Task.Delay(TimeSpan.FromSeconds(15), RealBuildHarness.Ct);
                p = await run.ProvenanceAsync(job);
            }
            Assert.Equal(bundle.AgentBuildId, p.AgentVersion);
            Assert.Contains("Pro", p.WindowsEdition, StringComparison.Ordinal);
            Assert.True(p.RealTimeProtectionEnabled == true, $"real-time protection still reads {p.RealTimeProtectionEnabled?.ToString() ?? "unknown"} after 5 min; the refusal cannot be shown");
            run.Progress($"guest: {p.WindowsEdition} {p.WindowsBuild}, agent {p.AgentVersion}, real-time protection on");

            await run.AssertProvisionLogCleanAsync(job, configuring);

            // Seal with real-time protection on: refused, the job stays Configuring and the Seal button comes back.
            var seal = await run.Admin.PostAsJsonAsync($"/api/admin/template-jobs/{job}/seal", new { }, RealBuildHarness.Ct);
            Assert.Equal(HttpStatusCode.Accepted, seal.StatusCode);
            var refused = await run.WaitForAsync(job,
                d => d.GetProperty("message").GetString() == TemplateCoordinator.DefenderStillOn || RealBuildHarness.IsTerminal(d.GetProperty("state").GetString()!),
                TimeSpan.FromMinutes(10));
            Assert.True(refused.GetProperty("state").GetString() == "Configuring", $"after the seal the job is {RealBuildHarness.Describe(refused)}");
            Assert.False(refused.GetProperty("sealRequested").GetBoolean());
            await Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var still = await run.JobAsync(job);
            Assert.Equal("Configuring", still.GetProperty("state").GetString());
            Assert.Equal(TemplateCoordinator.DefenderStillOn, still.GetProperty("message").GetString());
            Assert.False(still.GetProperty("sealRequested").GetBoolean());
            run.Progress("seal refused: Defender real-time protection is still on");

            // Discard: Cancelled, no version, and nothing of the build is left on the host but its provisioning log.
            var cancel = await run.Admin.PostAsync($"/api/admin/template-jobs/{job}/cancel", null, RealBuildHarness.Ct);
            Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
            var ended = await run.WaitForAsync(job, d => RealBuildHarness.IsTerminal(d.GetProperty("state").GetString()!), TimeSpan.FromMinutes(10));
            Assert.Equal("Cancelled", ended.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, ended.GetProperty("resultVersionNumber").ValueKind);

            var template = await run.TemplateAsync(templateId);
            Assert.Equal(0, template.GetProperty("versions").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, template.GetProperty("current").ValueKind);
            await run.AssertNothingLeftAsync(job);
            Assert.True(File.Exists(run.Build.ProvisionLogPathOf(job)), "the provisioning log of a discarded build is kept");

            // The empty template can go, and so can the media (no job uses it any more).
            Assert.Equal(HttpStatusCode.NoContent, (await run.Admin.DeleteAsync($"/api/admin/templates/{templateId}", RealBuildHarness.Ct)).StatusCode);
            await run.DeleteMediaAsync(media);
            run.Progress("discarded: no version, no domain, no links, no disk, no work directory, slot free");
        });
    }
}

/// <summary>
/// One real Build run's host and its cleanup, shared by <see cref="RealTemplateBuildTests"/> and
/// <see cref="RealTemplateBuildAcceptanceTests"/>. <see cref="RunAsync"/> boots a real-mode host with workers (its media library
/// and job logs in a directory of the run's own, private fetches allowed, the build's guest files taken from this checkout) and a
/// loopback server that streams <c>MALDEX_WIN_ISO</c>. Afterwards, whatever happened, it cancels the run's jobs, disposes the host,
/// force-removes and reports any VM, link or namespace of theirs, deletes their disks and work directories, unmounts and deletes
/// the run's directory, and checks the base directory and the ISO unchanged.
/// </summary>
internal sealed partial class RealBuildHarness(SqlServerFixture sql, ITestOutputHelper output)
{
    public const string BaseTemplateId = "win11-26h2";
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(10);
    /// <summary>Request to Configuring: the install is capped at 3 h (BuildOptions.InstallTimeout); on this host it takes about 15-25 min.</summary>
    public static readonly TimeSpan ConfiguringTimeout = TimeSpan.FromHours(3.5);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(60);

    /// <summary>This checkout: the build's guest files (Autounattend.xml, Provision.ps1) are the code under test.</summary>
    private static string RepoRoot => RealVmE2ETests.Root;

    /// <summary>
    /// The checkout that holds the real <c>.data</c>: the repository root, or, when <c>.data</c> is a symlink (a git worktree sharing
    /// the main checkout's image), the directory it points into. The host runs with it as <c>Maldex:Real:Root</c> (107-byte socket limit).
    /// </summary>
    public static string Root
    {
        get
        {
            var data = new DirectoryInfo(Path.Combine(RepoRoot, ".data"));
            return data.LinkTarget is null ? RepoRoot : Path.GetDirectoryName(data.ResolveLinkTarget(returnFinalTarget: true)!.FullName)!;
        }
    }
    private static string BaseDir => Path.Combine(Root, ".data", "vm", "base");
    private static string AgentDir => Path.Combine(Root, ".data", "agent");
    private static string RunNetDir => Path.Combine(Root, ".data", "vm", "run", "net");
    private static string? Iso => Environment.GetEnvironmentVariable("MALDEX_WIN_ISO");

    public static void RequireRealStack(bool interactive = false)
    {
        if (interactive)
            Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_E2E_INTERACTIVE") == "1",
                "Interactive Build acceptance skipped: someone must turn off Defender in the console. Set MALDEX_E2E_INTERACTIVE=1; run ./build.sh e2e-build-acceptance.");
        var run = interactive ? "./build.sh e2e-build-acceptance" : "./build.sh e2e-templates";
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_TEMPLATES") == "1" && !string.IsNullOrWhiteSpace(Iso) && File.Exists(Iso),
            $"Real Build E2E skipped: it needs MALDEX_REAL_TEMPLATES=1 and MALDEX_WIN_ISO set to a Windows 11 installer ISO (got '{Iso}'). Run MALDEX_WIN_ISO=/path/to/win11.iso {run}.");
        RealVmE2ETests.RequireRealStack();
        var missing = new[] { "Maldex.Agent.exe", "Maldex.Agent.version" }.Where(f => !File.Exists(Path.Combine(AgentDir, f))).ToList();
        Assert.SkipWhen(missing.Count > 0,
            $"Real Build E2E skipped: the guest agent build is missing in {AgentDir} ({string.Join(", ", missing)}). Run ./build.sh agent, then {run}.");
    }

    // ---- the run --------------------------------------------------------------------------------------------------------

    /// <summary>What a test body drives: the host, an admin client and the steps of a build.</summary>
    internal sealed class Run(RealBuildHarness harness, MaldexAppFactory host, HttpClient admin, Uri isoUrl, long isoBytes)
    {
        public MaldexAppFactory Host { get; } = host;
        public HttpClient Admin { get; } = admin;
        public ProvenanceRoundTrips RoundTrips => harness._roundTrips;
        public BuildOptions Build => Host.Services.GetRequiredService<IOptions<BuildOptions>>().Value;
        public void Progress(string line) => harness.Progress(line);

        public Task<ComponentBundle> BundleAsync() =>
            ComponentBundle.LoadAsync(Host.Services.GetRequiredService<IOptions<TemplateJobOptions>>().Value, Ct);

        /// <summary>Fetches the ISO into the run's media library and waits for Ready: the Pro image and its index must be detected.</summary>
        public async Task<Guid> FetchIsoAsync()
        {
            var started = DateTimeOffset.UtcNow;
            var resp = await Admin.PostAsJsonAsync("/api/admin/media/fetch", new { url = isoUrl.ToString(), kind = "OsInstaller", fileName = "win11.iso" }, Ct);
            Assert.True(resp.StatusCode == HttpStatusCode.Accepted, $"fetch: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
            var id = (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
            harness._media.Add(id);
            Progress($"fetching {isoUrl} ({isoBytes / (1024.0 * 1024 * 1024):0.0} GiB) as media {id:N}");

            JsonElement media;
            var lastReport = DateTimeOffset.MinValue;
            while (true)
            {
                media = await Admin.GetFromJsonAsync<JsonElement>($"/api/admin/media/{id}", Ct);
                var status = media.GetProperty("status").GetString();
                if (status != "Receiving") break;
                if (DateTimeOffset.UtcNow - lastReport > TimeSpan.FromMinutes(1))
                {
                    lastReport = DateTimeOffset.UtcNow;
                    Progress($"media {id:N}: {media.GetProperty("receivedBytes").GetInt64() / (1024 * 1024)} MiB received");
                }
                Assert.True(DateTimeOffset.UtcNow - started < FetchTimeout, $"media {id} still Receiving after {FetchTimeout}");
                await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            }
            Assert.True(media.GetProperty("status").GetString() == "Ready",
                $"media ended {media.GetProperty("status").GetString()}: {media.GetProperty("failureReason").GetString()}");
            Progress($"fetched and inspected in {(DateTimeOffset.UtcNow - started).TotalSeconds:0} s");

            Assert.Equal(isoBytes, media.GetProperty("size").GetInt64());
            Assert.Matches("^[0-9a-f]{64}$", media.GetProperty("sha256").GetString());
            var images = media.GetProperty("images").EnumerateArray().ToList();
            foreach (var i in images)
                Progress($"image {i.GetProperty("index").GetInt32()}: {i.GetProperty("name").GetString()} {i.GetProperty("build").GetString()} {i.GetProperty("architecture").GetString()}"
                    + (i.GetProperty("supported").GetBoolean() ? "" : $" (unsupported: {i.GetProperty("unsupportedReason").GetString()})"));
            var pro = images.SingleOrDefault(i => i.GetProperty("name").GetString() == "Windows 11 Pro");
            Assert.True(pro.ValueKind == JsonValueKind.Object, "the Pro image was not detected");
            Assert.True(pro.GetProperty("supported").GetBoolean());
            Assert.Equal(pro.GetProperty("index").GetInt32(), media.GetProperty("preferredImageIndex").GetInt32());
            return id;
        }

        /// <summary>Starts a Build of <paramref name="templateId"/> (new, or existing) from the media with the preferred edition (Pro).</summary>
        public async Task<Guid> StartBuildAsync(string templateId, string displayName, Guid media, bool publish)
        {
            var resp = await Admin.PostAsJsonAsync("/api/admin/templates/build", new
            {
                templateId, displayName, osMediaId = media, editionIndex = (int?)null, applicationMediaIds = Array.Empty<Guid>(), publishOnSuccess = publish,
            }, Ct);
            Assert.True(resp.StatusCode == HttpStatusCode.Accepted, $"build: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
            var dto = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
            var job = dto.GetProperty("id").GetGuid();
            harness._jobs.Add(job);
            Assert.Equal("Build", dto.GetProperty("kind").GetString());
            Assert.Equal("Windows 11 Pro", dto.GetProperty("edition").GetString());
            Progress($"build job {job:N} of {templateId} (publish {publish}); provisioning log: {Build.ProvisionLogPathOf(job)}");
            return job;
        }

        public async Task<JsonElement> JobAsync(Guid job) =>
            await Admin.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{job}", Ct);

        public async Task<JsonElement> TemplateAsync(string id) =>
            (await Admin.GetFromJsonAsync<JsonElement>("/api/admin/templates", Ct)).EnumerateArray().Single(t => t.GetProperty("id").GetString() == id);

        /// <summary>Polls the job until <paramref name="done"/>; prints every change of state or message and how long each state lasted.</summary>
        public async Task<JsonElement> WaitForAsync(Guid job, Func<JsonElement, bool> done, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            var phases = harness.PhasesOf(job);
            string? last = null;
            while (true)
            {
                var dto = await JobAsync(job);
                var state = dto.GetProperty("state").GetString()!;
                var now = DateTimeOffset.UtcNow;
                if (phases.Count == 0 || phases[^1].State != state)
                {
                    if (phases.Count > 0) Progress($"phase {phases[^1].State} took {Minutes(now - phases[^1].Since)}");
                    phases.Add((state, now));
                }
                var line = $"{state}: {dto.GetProperty("message").GetString()}";
                if (line != last) Progress($"job {job:N} {line}");
                last = line;
                if (done(dto)) return dto;
                Assert.True(now < deadline, $"job {job} still {line} after {timeout}");
                await Task.Delay(PollEvery, Ct);
            }
        }

        /// <summary>Waits (up to <see cref="ConfiguringTimeout"/> from the request) for Configuring; logs the time per phase.</summary>
        public async Task<JsonElement> WaitForConfiguringAsync(Guid job)
        {
            var requested = DateTimeOffset.UtcNow;
            var dto = await WaitForAsync(job, d => d.GetProperty("state").GetString() is var s && (s == "Configuring" || IsTerminal(s!)), ConfiguringTimeout);
            Assert.True(dto.GetProperty("state").GetString() == "Configuring", $"build job ended {Describe(dto)}; provisioning log: {Build.ProvisionLogPathOf(job)}");
            var phases = harness.PhasesOf(job);
            Progress($"Configuring after {Minutes(DateTimeOffset.UtcNow - requested)}; per phase: "
                + string.Join(", ", phases.Zip(phases.Skip(1)).Select(p => $"{p.First.State} {Minutes(p.Second.Since - p.First.Since)}")));
            return dto;
        }

        /// <summary>The guest's own report, on a channel of the test's (the coordinator holds none while Configuring).</summary>
        public async Task<Maldex.Core.Agent.ProvenanceMessage> ProvenanceAsync(Guid job)
        {
            var o = Host.Services.GetRequiredService<IOptions<TemplateJobOptions>>().Value;
            await using var channel = await Host.Services.GetRequiredService<IAgentChannelFactory>().ConnectAsync(VmHandle.For(job), Ct);
            await using var session = new GuestSession(channel);
            return await session.AskProvenanceWithRetryAsync(o.ProvenanceRetryInterval, TimeSpan.FromMinutes(2), TimeProvider.System, Ct);
        }

        /// <summary>The kept provisioning log ran every step without a failure.</summary>
        public async Task AssertProvisionLogCleanAsync(Guid job, JsonElement dto)
        {
            Assert.True(dto.GetProperty("hasProvisionLog").GetBoolean(), "the job has no provisioning log");
            var resp = await Admin.GetAsync($"/api/admin/template-jobs/{job}/provision-log", Ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var log = await resp.Content.ReadAsStringAsync(Ct);
            var failures = FailedStep().Matches(log).Select(m => m.Value.Trim()).ToList();
            Assert.True(failures.Count == 0, "the provisioning log has failed steps:\n" + string.Join("\n", failures));
            Assert.Contains("Provisioning complete.", log, StringComparison.Ordinal);
            foreach (var warning in Warning().Matches(log).Select(m => m.Value.Trim())) Progress("provisioning " + warning); // best-effort steps
        }

        /// <summary>After a discard: no VM, link or namespace of the job, no disk files, no work directory, and the VM slot free.</summary>
        public async Task AssertNothingLeftAsync(Guid job)
        {
            var left = LeftOver.None;
            for (var i = 0; i < 15; i++)
            {
                left = await RealVmE2ETests.FindAsync(job, Ct);
                if (left.IsEmpty) break;
                await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            }
            Assert.True(left.IsEmpty, $"the discarded build left {left}");
            Assert.Empty(JobFiles(job));
            Assert.False(Directory.Exists(TemplateCoordinator.WorkDirOf(Build, job)), "the build's work directory is still there");
            await using var db = Db(Host);
            Assert.Equal(0, await VmSlots.BusyAsync(db, Ct));
        }

        public async Task DeleteMediaAsync(Guid id)
        {
            var media = await Admin.GetFromJsonAsync<JsonElement>($"/api/admin/media/{id}", Ct);
            var file = Path.Combine(harness.MediaRoot, media.GetProperty("sha256").GetString() + ".iso");
            Assert.True(File.Exists(file), $"the media file {file} is missing");
            Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/api/admin/media/{id}", Ct)).StatusCode);
            Assert.False(File.Exists(file), "deleting the media left its file");
            harness._media.Remove(id);
        }
    }

    [GeneratedRegex(@"(?m)^\s*(ERROR: .*|FATAL: .*|PROVISIONING FAILED.*)$")]
    private static partial Regex FailedStep();

    [GeneratedRegex(@"(?m)^\s*WARNING: .* failed \(continuing\).*$")]
    private static partial Regex Warning();

    private readonly List<Guid> _jobs = [];
    private readonly List<Guid> _media = [];
    private readonly Dictionary<Guid, List<(string State, DateTimeOffset Since)>> _phases = [];
    private readonly ProvenanceRoundTrips _roundTrips = new();
    private readonly string _runDir = Path.Combine(Root, ".data", "e2e-build-" + Guid.NewGuid().ToString("N")[..12]);
    private string MediaRoot => Path.Combine(_runDir, "media");

    private List<(string State, DateTimeOffset Since)> PhasesOf(Guid job) =>
        _phases.TryGetValue(job, out var list) ? list : _phases[job] = [];

    /// <summary>
    /// Runs <paramref name="body"/> against a real-mode host. <paramref name="kestrel"/> serves it on 127.0.0.1:<paramref name="port"/>
    /// (the interactive console); otherwise it stays in memory. The cleanup always runs; a leak is reported with any test failure.
    /// </summary>
    public async Task RunAsync(Func<Run, Task> body, int? port = null, IReadOnlyDictionary<string, string?>? extra = null)
    {
        var iso = Path.GetFullPath(Iso!);
        var isoBefore = Stat(iso);
        var before = Snapshot();
        var domainsBefore = await DomainsAsync(CancellationToken.None);
        var runNetBefore = RunNetDirs();
        Directory.CreateDirectory(_runDir);

        var settings = new Dictionary<string, string?>
        {
            ["Maldex:Mode"] = "Real",
            ["Maldex:Workers:Enabled"] = "true",
            ["Maldex:Real:Root"] = Root,
            ["Maldex:Templates:Jobs:AgentExePath"] = Path.Combine(AgentDir, "Maldex.Agent.exe"),
            ["Maldex:Templates:Jobs:AgentVersionPath"] = Path.Combine(AgentDir, "Maldex.Agent.version"),
            ["Maldex:Templates:Jobs:SysmonConfigPath"] = Path.Combine(RepoRoot, "deploy", "guest", "provision", "sysmon-config.xml"),
            ["Maldex:Templates:Jobs:MitmCaPemPath"] = Path.Combine(Root, ".data", "mitm", "ca", "mitmproxy-ca-cert.pem"),
            ["Maldex:Templates:Build:AutounattendPath"] = Path.Combine(RepoRoot, "deploy", "guest", "autounattend", "Autounattend.xml"),
            ["Maldex:Templates:Build:ProvisionScriptPath"] = Path.Combine(RepoRoot, "deploy", "guest", "provision", "Provision.ps1"),
            ["Maldex:Templates:Build:OfficeConfigPath"] = Path.Combine(RepoRoot, "deploy", "guest", "provision", "office-config.xml"),
            ["Maldex:Templates:Build:JobLogDir"] = Path.Combine(_runDir, "jobs"),
            ["Maldex:Media:Root"] = MediaRoot,
            // The ISO comes from a loopback server; production refuses private destinations.
            ["Maldex:Media:AllowPrivateFetch"] = "true",
            // Never let retention retire (and then delete) the imported v1, whose files are the hand-prepared image.
            ["Maldex:Templates:KeepPrevious"] = "10",
        };
        foreach (var (k, v) in extra ?? new Dictionary<string, string?>()) settings[k] = v;

        IsoServer? server = null;
        var host = new RecordingHost(sql, _roundTrips, settings);
        if (port is { } p) host.UseKestrel(o => o.Listen(IPAddress.Loopback, p));
        Exception? failure = null;
        try
        {
            Ct.ThrowIfCancellationRequested();
            server = await IsoServer.StartAsync(iso);
            using var admin = host.CreateClient(UserRole.Admin, "admin");
            await body(new Run(this, host, admin, server.Url, isoBefore.Length));
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        var problems = new List<string>();
        foreach (var job in _jobs)
        {
            try { await CancelIfActiveAsync(host, job); }
            catch (Exception ex) { problems.Add($"cancelling job {job:N} failed: {ex.Message}"); }
        }
        foreach (var id in _media)
        {
            try
            {
                using var admin = host.CreateClient(UserRole.Admin, "admin");
                using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await admin.DeleteAsync($"/api/admin/media/{id}", bound.Token);
            }
            catch (Exception ex) { Progress($"deleting media {id:N} through the API failed ({ex.Message}); its directory goes below"); }
        }
        // Kept for a failure report: the run's directory (and the log in it) is deleted below.
        foreach (var job in _jobs) KeepProvisionLog(host, job);
        try { host.Dispose(); }
        catch (Exception ex) { problems.Add($"disposing the host failed: {ex.Message}"); }
        if (server is not null)
        {
            try { await server.DisposeAsync(); }
            catch (Exception ex) { problems.Add($"stopping the ISO server failed: {ex.Message}"); }
        }

        using (var bound = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            try
            {
                foreach (var job in _jobs)
                {
                    var left = await RealVmE2ETests.FindAsync(job, bound.Token);
                    if (left.IsEmpty) continue;
                    problems.Add($"job {job:N} left behind: {left} (force-removed)");
                    await RealVmE2ETests.ForceRemoveAsync(left, bound.Token);
                }
                // A verification boot has an id of its own: any new maldex-* domain is a leak.
                foreach (var d in (await DomainsAsync(bound.Token)).Except(domainsBefore))
                {
                    problems.Add($"domain {d} left behind (destroyed)");
                    await RealVmE2ETests.RunToolAsync(bound.Token, "virsh", "-c", "qemu:///session", "destroy", d);
                    await RealVmE2ETests.RunToolAsync(bound.Token, "virsh", "-c", "qemu:///session", "undefine", "--nvram", d);
                }
            }
            catch (Exception ex) { problems.Add($"leak check failed: {ex.Message}"); }

            // The router keeps a run's net directory for collection; nobody collects a template job's. Only this run's are removed.
            foreach (var dir in RunNetDirs().Except(runNetBefore))
            {
                try { await RealVmE2ETests.RunToolAsync(bound.Token, "sudo", "-n", "rm", "-rf", "--", Path.Combine(RunNetDir, dir)); }
                catch (Exception ex) { problems.Add($"removing the run net directory {dir} failed: {ex.Message}"); }
            }

            // A mount the inspector left (it never should) is removed before its directory.
            foreach (var mount in MountsUnder(_runDir))
            {
                problems.Add($"loop mount {mount} left behind (unmounted)");
                try { await RealVmE2ETests.RunToolAsync(bound.Token, "sudo", "-n", "umount", "--", mount); }
                catch (Exception ex) { problems.Add($"unmounting {mount} failed: {ex.Message}"); }
            }
        }

        var buildOptions = new BuildOptions { WorkDir = Path.Combine(Root, ".data", "vm", "build") };
        foreach (var job in _jobs)
        {
            foreach (var file in JobFiles(job))
            {
                try { File.Delete(file); }
                catch (Exception ex) { problems.Add($"deleting {file} failed: {ex.Message}"); }
            }
            var work = TemplateCoordinator.WorkDirOf(buildOptions, job);
            if (Directory.Exists(work))
            {
                problems.Add($"the work directory {work} was left (deleted)");
                try { Directory.Delete(work, recursive: true); }
                catch (Exception ex) { problems.Add($"deleting {work} failed: {ex.Message}"); }
            }
        }
        if (MountsUnder(_runDir).Count == 0)
        {
            try { Directory.Delete(_runDir, recursive: true); }
            catch (Exception ex) { problems.Add($"deleting the run directory {_runDir} failed: {ex.Message}"); }
        }
        else problems.Add($"the run directory {_runDir} still holds a mount; not deleted");

        var after = Snapshot();
        if (!after.SequenceEqual(before))
            problems.Add($"the base directory changed: before [{string.Join(", ", before)}], after [{string.Join(", ", after)}]");
        if (Stat(iso) != isoBefore) problems.Add($"the ISO {iso} changed: before {isoBefore}, after {Stat(iso)}");

        try
        {
            foreach (var line in _roundTrips.Describe(vm => (_jobs.Any(j => vm == VmHandle.For(j).Name) ? "job " : "verification boot ") + vm))
                Progress("provenance: " + line);
        }
        catch (Exception ex)
        {
            try { Progress($"provenance: could not describe the round trips ({ex.Message})"); } catch (Exception) { /* output gone */ }
        }

        var leak = problems.Count == 0 ? null : string.Join("; ", problems);
        if (failure is not null && leak is not null) throw new AggregateException($"Build E2E failed AND cleanup reported problems: {leak}", failure, new InvalidOperationException(leak));
        if (leak is not null) throw new InvalidOperationException(leak);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Copies a job's provisioning log to TestResults (git-ignored) before the run's directory goes. Best effort.</summary>
    private void KeepProvisionLog(MaldexAppFactory host, Guid job)
    {
        try
        {
            var log = host.Services.GetRequiredService<IOptions<BuildOptions>>().Value.ProvisionLogPathOf(job);
            if (!File.Exists(log)) return;
            var dir = Path.Combine(RepoRoot, "TestResults");
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, $"e2e-build-{job:N}-provision.log");
            File.Copy(log, copy, overwrite: true);
            Progress($"provisioning log kept as {copy}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException)
        {
            Progress($"could not keep the provisioning log of {job:N}: {ex.Message}");
        }
    }

    /// <summary>
    /// Asks a still-active job to cancel and waits up to 10 min (an install is torn down) for a terminal state. A job that is gone
    /// (deleting its empty template deletes it) is done.
    /// </summary>
    private static async Task CancelIfActiveAsync(MaldexAppFactory f, Guid job)
    {
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        for (var i = 0; ; i++)
        {
            using var resp = await admin.GetAsync($"/api/admin/template-jobs/{job}", bound.Token);
            if (resp.StatusCode == HttpStatusCode.NotFound) return;
            resp.EnsureSuccessStatusCode();
            var dto = await resp.Content.ReadFromJsonAsync<JsonElement>(bound.Token);
            if (IsTerminal(dto.GetProperty("state").GetString()!)) return;
            if (i % 6 == 0) await admin.PostAsync($"/api/admin/template-jobs/{job}/cancel", null, bound.Token);
            await Task.Delay(TimeSpan.FromSeconds(5), bound.Token);
        }
    }

    public static bool IsTerminal(string state) => state is "Published" or "Sealed" or "Failed" or "Cancelled";

    public static string Describe(JsonElement job) =>
        $"{job.GetProperty("state").GetString()} (message: {job.GetProperty("message").GetString()}; failure: {job.GetProperty("failureReason").GetString()})";

    private static string Minutes(TimeSpan t) => $"{t.TotalMinutes:0.0} min";

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex JobStem();

    /// <summary>The disk files of a job's stem (blank disk, sealed version, partial copy). Only a 32-hex stem, never the base image.</summary>
    public static List<string> JobFiles(Guid job)
    {
        var stem = TemplateCoordinator.StemOf(job);
        Assert.Matches(JobStem(), stem);
        Assert.NotEqual(BaseTemplateId, stem);
        return new[] { ".qcow2", ".vars.fd", ".qcow2.tmp", ".vars.fd.tmp" }
            .Select(ext => Path.Combine(BaseDir, stem + ext)).Where(File.Exists).ToList();
    }

    /// <summary>Every file of the base directory with its size, mode and write time (so a change to the base image shows too).</summary>
    private static List<string> Snapshot() =>
        new DirectoryInfo(BaseDir).EnumerateFiles().OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => $"{f.Name} {f.Length} {f.UnixFileMode} {f.LastWriteTimeUtc:O}").ToList();

    private static (long Length, UnixFileMode Mode, DateTime WrittenUtc) Stat(string file)
    {
        var f = new FileInfo(file);
        return (f.Length, f.UnixFileMode, f.LastWriteTimeUtc);
    }

    [GeneratedRegex("^mxn[0-9a-f]{12}$")]
    private static partial Regex RunNamespace();

    private static List<string> RunNetDirs() =>
        Directory.Exists(RunNetDir)
            ? Directory.EnumerateDirectories(RunNetDir).Select(Path.GetFileName).OfType<string>().Where(n => RunNamespace().IsMatch(n)).ToList()
            : [];

    /// <summary>Mount points at or below <paramref name="dir"/> (field 5 of /proc/self/mountinfo).</summary>
    private static List<string> MountsUnder(string dir) =>
        File.ReadAllLines("/proc/self/mountinfo").Select(l => l.Split(' ')).Where(f => f.Length > 4).Select(f => f[4].Replace("\\040", " "))
            .Where(m => m == dir || m.StartsWith(dir + "/", StringComparison.Ordinal)).ToList();

    private static async Task<List<string>> DomainsAsync(CancellationToken ct) =>
        RealVmE2ETests.Lines(await RealVmE2ETests.RunToolAsync(ct, "virsh", "-c", "qemu:///session", "list", "--all", "--name"))
            .Where(d => d.StartsWith("maldex-", StringComparison.Ordinal)).ToList();

    public static MaldexDbContext Db(MaldexAppFactory f) =>
        new(new DbContextOptionsBuilder<MaldexDbContext>().UseSqlServer(f.ConnectionString).Options);

    public void Progress(string line)
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

    /// <summary>Serves one file read-only at <c>http://127.0.0.1:&lt;port&gt;/win11.iso</c>, streamed from disk (never read into memory).</summary>
    private sealed class IsoServer(WebApplication app, Uri url) : IAsyncDisposable
    {
        public Uri Url { get; } = url;

        public static async Task<IsoServer> StartAsync(string isoPath)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production", ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
            var app = builder.Build();
            app.MapGet("/win11.iso", () => TypedResults.PhysicalFile(isoPath, "application/octet-stream"));
            await app.StartAsync(Ct);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new IsoServer(app, new Uri(new Uri(address), "/win11.iso"));
        }

        public async ValueTask DisposeAsync()
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await app.StopAsync(bound.Token);
            await app.DisposeAsync();
        }
    }
}

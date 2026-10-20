using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Infrastructure.Real;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Maldex.Integration.Tests.E2E;

/// <summary>
/// Harmless real-VM end-to-end suite: submit -> orchestrator (real libvirt, netns, agent, capture) -> processing -> assertions on the
/// database rows, all through the real DI (<c>Maldex:Mode=Real</c>, <c>Simulated</c> network mode, workers enabled). Run it with
/// <c>./build.sh e2e</c>. Only harmless files are used (see deploy/guest/samples/README.md); it never runs malware.
/// Skipped unless MALDEX_REAL_VM=1 and MALDEX_REAL_NET=1 and the win11-26h2 base image (qcow2 + vars.fd) exists.
/// </summary>
[Collection("sql")]
public sealed class RealVmE2ETests(SqlServerFixture sql, ITestOutputHelper output) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(20);
    private readonly List<MaldexAppFactory> _factories = [];

    public void Dispose() { foreach (var f in _factories) f.Dispose(); }

    // ---- gating -------------------------------------------------------------------------------------------------

    private static string Root => RealPaths.FindRoot(AppContext.BaseDirectory);
    private static string SamplesDir => Path.Combine(Root, "deploy", "guest", "samples");

    private static void RequireRealStack()
    {
        var baseDir = Path.Combine(Root, ".data", "vm", "base");
        var missing = new[] { "win11-26h2.qcow2", "win11-26h2.vars.fd" }.Where(f => !File.Exists(Path.Combine(baseDir, f))).ToList();
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_VM") == "1"
            && Environment.GetEnvironmentVariable("MALDEX_REAL_NET") == "1" && missing.Count == 0,
            "Real-VM E2E skipped: set MALDEX_REAL_VM=1 and MALDEX_REAL_NET=1 and build the guest image "
            + $"(MALDEX_WIN_ISO=/path/to/Win11_26H2.iso ./build.sh build-guest; missing in {baseDir}: {(missing.Count == 0 ? "none" : string.Join(", ", missing))}). Then run ./build.sh e2e.");
    }

    // ---- harness ------------------------------------------------------------------------------------------------

    private MaldexAppFactory NewFactory()
    {
        var f = new MaldexAppFactory(sql, workers: true, settings: new Dictionary<string, string?>
        {
            ["Maldex:Mode"] = "Real",
            ["Maldex:Workers:Enabled"] = "true",
        });
        _factories.Add(f);
        return f;
    }

    private static MaldexDbContext Db(MaldexAppFactory f) =>
        new(new DbContextOptionsBuilder<MaldexDbContext>().UseSqlServer(f.ConnectionString).Options);

    private static bool IsTerminal(RunStatus s) => s is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;

    /// <summary>
    /// Submits a file or URL in Simulated mode, waits for a terminal status and hands the finished task to <paramref name="assert"/>.
    /// Whatever happens (assertion failure, timeout, exception) the run is stopped, the host is shut down, and the leak check runs;
    /// a leak is reported in addition to the original failure.
    /// </summary>
    private async Task RunAsync(string? fileName, byte[]? file, string? url, Func<AnalysisTask, MaldexDbContext, Task> assert)
    {
        var f = NewFactory();
        Guid taskId = default;
        Exception? failure = null;
        try
        {
            using var admin = f.CreateClient(UserRole.Admin, "admin");
            var orgResp = await admin.PostAsJsonAsync("/api/orgs", new { name = "e2e", retentionDays = 30 }, Ct);
            orgResp.EnsureSuccessStatusCode();
            var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

            using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
            using var form = new MultipartFormDataContent
            {
                { new StringContent(orgId.ToString()), "orgId" },
                { new StringContent("Simulated"), "networkMode" },
                { new StringContent("60"), "durationSec" },
                { new StringContent("false"), "interactive" },
            };
            if (url is not null) form.Add(new StringContent(url), "url");
            if (file is not null)
            {
                var part = new ByteArrayContent(file);
                part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(part, "file", fileName!);
            }
            var resp = await analyst.PostAsync("/api/tasks", form, Ct);
            Assert.Equal(System.Net.HttpStatusCode.Created, resp.StatusCode);
            taskId = (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

            var deadline = DateTimeOffset.UtcNow + RunTimeout;
            AnalysisTask task;
            while (true)
            {
                await using var db = Db(f);
                task = await db.Tasks.AsNoTracking().Include(t => t.StatusHistory).SingleAsync(t => t.Id == taskId, Ct);
                if (IsTerminal(task.Status)) break;
                Assert.True(DateTimeOffset.UtcNow < deadline, $"task {taskId} still {task.Status} after {RunTimeout}");
                await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            }

            ReportBootTime(task);
            Assert.True(task.Status == RunStatus.Completed, $"task ended {task.Status}: {task.FailureReason}");

            await using var checks = Db(f);
            // The isolation self-test passed before anything was delivered.
            Assert.Contains(await checks.AuditLogs.AsNoTracking().Where(a => a.Target == taskId.ToString()).Select(a => a.Action).ToListAsync(Ct),
                a => a == "isolation.selftest.passed");
            await assert(task, checks);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // ---- cleanup: always runs; every phase has its own bound (see E2ECleanup) and none uses the test's token ----
        var leak = await new E2ECleanup(
            stop: ct => taskId == default ? Task.CompletedTask : StopAndWaitAsync(f, taskId, ct),
            dispose: () => { f.Dispose(); _factories.Remove(f); },
            find: ct => taskId == default ? Task.FromResult(LeftOver.None) : FindAsync(taskId, ct),
            forceRemove: ForceRemoveAsync,
            stopBound: TimeSpan.FromSeconds(90), checkBound: TimeSpan.FromMinutes(2), removeBound: TimeSpan.FromMinutes(1),
            pollInterval: TimeSpan.FromSeconds(2), checkAttempts: 15).RunAsync();

        if (failure is not null && leak is not null) throw new AggregateException($"E2E failed AND cleanup reported problems: {leak}", failure, new InvalidOperationException(leak));
        if (leak is not null) throw new InvalidOperationException(leak);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Asks a still-active task to stop (cancel when queued, stop when preparing/running) and waits, bounded, for a terminal status.</summary>
    private static async Task StopAndWaitAsync(MaldexAppFactory f, Guid taskId, CancellationToken ct)
    {
        try
        {
            using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
            for (var i = 0; i < 120; i++)
            {
                await using var db = Db(f);
                var status = await db.Tasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => t.Status).SingleAsync(ct);
                if (IsTerminal(status)) return;
                if (i % 10 == 0)
                {
                    var path = status == RunStatus.Queued ? "cancel" : "stop";
                    await analyst.PostAsync($"/api/tasks/{taskId}/{path}", null, ct);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best effort: the leak check below still runs */ }
    }

    /// <summary>Success criterion 1 is measured, not asserted, in dev: Preparing -> agent Ready (the Running transition).</summary>
    private void ReportBootTime(AnalysisTask task)
    {
        var preparing = task.StatusHistory.FirstOrDefault(h => h.To == RunStatus.Preparing);
        var running = task.StatusHistory.FirstOrDefault(h => h.To == RunStatus.Running);
        var line = preparing is not null && running is not null
            ? $"{DateTimeOffset.UtcNow:O} Preparing -> agent Ready: {(running.At - preparing.At).TotalSeconds:0.0} s (task {task.Id})"
            : $"{DateTimeOffset.UtcNow:O} Preparing -> agent Ready: not measurable (task {task.Id} never reached Running)";
        output.WriteLine(line);
        Console.WriteLine(line); // visible on green runs too
        try
        {
            var dir = Path.Combine(Root, "TestResults");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "e2e-boot-times.txt"), line + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- leak check, scoped to this run --------------------------------------------------------------------------

    private static async Task<string> RunToolAsync(CancellationToken ct, string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi)!; }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"'{file}' is not installed or not on PATH ({ex.Message}); run ./build.sh real-setup.", ex);
        }
        using (p)
        {
            try
            {
                var stdout = p.StandardOutput.ReadToEndAsync(ct);
                _ = p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                return await stdout;
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
        }
    }

    private static string[] Lines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Resources of this run only: domain maldex-{id:N} and links/namespaces carrying h = id:N[..12] (mxb/mxt/mxv/mxg/mxn/mxw + h).</summary>
    private static async Task<LeftOver> FindAsync(Guid taskId, CancellationToken ct)
    {
        var n = taskId.ToString("N");
        var h = n[..12];
        var vm = "maldex-" + n;
        string[] prefixes = ["mxb", "mxt", "mxv", "mxg", "mxn", "mxw"];
        bool Ours(string name) => prefixes.Any(p => name == p + h);
        var domains = Lines(await RunToolAsync(ct, "virsh", "-c", "qemu:///session", "list", "--all", "--name")).Where(d => d == vm).ToList();
        var links = Lines(await RunToolAsync(ct, "ip", "-o", "link", "show"))
            .Select(l => l.Split(':', 3)).Where(p => p.Length > 2).Select(p => p[1].Trim().Split('@')[0]).Where(Ours).ToList();
        var spaces = Lines(await RunToolAsync(ct, "ip", "netns", "list")).Select(l => l.Split(' ')[0]).Where(Ours).ToList();
        return new LeftOver(domains, links, spaces);
    }

    /// <summary>Only a positive integer greater than 1 may be handed to <c>kill -9</c> (never init, never a stray token).</summary>
    internal static bool IsKillablePid(string s) =>
        int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 1;

    private static async Task ForceRemoveAsync(LeftOver found, CancellationToken ct)
    {
        foreach (var d in found.Domains) await RunToolAsync(ct, "virsh", "-c", "qemu:///session", "destroy", d);
        foreach (var ns in found.Namespaces)
        {
            // Kill whatever still runs inside before deleting the namespace.
            foreach (var pid in Lines(await RunToolAsync(ct, "sudo", "-n", "ip", "netns", "pids", ns)).Where(IsKillablePid))
                await RunToolAsync(ct, "sudo", "-n", "kill", "-9", pid);
            await RunToolAsync(ct, "sudo", "-n", "ip", "netns", "del", ns);
        }
        foreach (var l in found.Links) await RunToolAsync(ct, "sudo", "-n", "ip", "link", "del", l);
    }

    // ---- tests --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Eicar_IsMalicious()
    {
        RequireRealStack();
        var b64 = await File.ReadAllTextAsync(Path.Combine(SamplesDir, "eicar.txt.b64"), Ct);
        await RunAsync("eicar.txt", Convert.FromBase64String(b64.Trim()), url: null, async (task, db) =>
        {
            Assert.Equal(Verdict.Malicious, task.Verdict);
            Assert.Contains(await db.Detections.AsNoTracking().Where(d => d.TaskId == task.Id).ToListAsync(Ct),
                d => d.Source == DetectionSource.Yara && d.Rule.Contains("EICAR", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task RegWriteScript_PersistenceHeuristic()
    {
        RequireRealStack();
        var script = await File.ReadAllBytesAsync(Path.Combine(SamplesDir, "regwrite.ps1"), Ct);
        await RunAsync("regwrite.ps1", script, url: null, async (task, db) =>
        {
            Assert.True(task.Verdict >= Verdict.Suspicious, $"verdict {task.Verdict}");
            Assert.Contains(await db.Detections.AsNoTracking().Where(d => d.TaskId == task.Id).ToListAsync(Ct),
                d => d.Source == DetectionSource.Heuristic && d.Rule == "persistence_run_key");
            Assert.Contains(await db.BehaviorEvents.AsNoTracking().Where(e => e.TaskId == task.Id && e.Type == EventType.Registry).ToListAsync(Ct),
                e => e.Data.Contains("MaldexTest", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task ControlledUrl_CapturesDnsFlowAndHttp()
    {
        RequireRealStack();
        var url = (await File.ReadAllTextAsync(Path.Combine(SamplesDir, "url.txt"), Ct)).Trim();
        await RunAsync(null, null, url, async (task, db) =>
        {
            var artifacts = await db.Artifacts.AsNoTracking().Where(a => a.TaskId == task.Id).Select(a => a.Kind).ToListAsync(Ct);
            Assert.Contains(ArtifactKind.Pcap, artifacts);
            Assert.Contains(ArtifactKind.MitmFlows, artifacts);

            Assert.Contains(await db.BehaviorEvents.AsNoTracking().Where(e => e.TaskId == task.Id && e.Type == EventType.Dns).ToListAsync(Ct),
                e => e.Summary.Contains("maldex-test.invalid", StringComparison.OrdinalIgnoreCase) || e.Data.Contains("maldex-test.invalid", StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(await db.NetworkFlows.AsNoTracking().Where(f => f.TaskId == task.Id).ToListAsync(Ct));
            Assert.Contains(await db.HttpTransactions.AsNoTracking().Where(h => h.TaskId == task.Id).ToListAsync(Ct),
                h => h.Host.Contains("maldex-test.invalid", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(await db.Detections.AsNoTracking().Where(d => d.TaskId == task.Id && d.Source == DetectionSource.Suricata).ToListAsync(Ct),
                d => d.Details != null && d.Details.Contains("sid 9000001", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// Needs Office in the image. The provisioning step does not leave a marker, so opt in explicitly:
    /// MALDEX_E2E_OFFICE=1 and MALDEX_E2E_OFFICE_SAMPLE=/path/to/calc-macro.doc (a macro-enabled document whose AutoOpen starts calc.exe).
    /// </summary>
    [Fact]
    public async Task CalcMacro_OfficeSpawnsProcess_Suspicious()
    {
        RequireRealStack();
        var sample = Environment.GetEnvironmentVariable("MALDEX_E2E_OFFICE_SAMPLE");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_E2E_OFFICE") == "1" && !string.IsNullOrWhiteSpace(sample) && File.Exists(sample),
            "Calc-macro E2E skipped: the image needs Office. Set MALDEX_E2E_OFFICE=1 and MALDEX_E2E_OFFICE_SAMPLE=/path/to/calc-macro.doc (see deploy/guest/README.md).");
        await RunAsync(Path.GetFileName(sample!), await File.ReadAllBytesAsync(sample!, Ct), url: null, async (task, db) =>
        {
            Assert.True(task.Verdict >= Verdict.Suspicious, $"verdict {task.Verdict}");
            Assert.Contains(await db.Detections.AsNoTracking().Where(d => d.TaskId == task.Id).ToListAsync(Ct),
                d => d.Source == DetectionSource.Heuristic && d.Rule == "office_spawns_process");
        });
    }
}

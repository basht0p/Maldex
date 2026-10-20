using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Infrastructure.Real;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    private sealed record RunResult(Guid TaskId, AnalysisTask Task, MaldexAppFactory Factory);

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

    /// <summary>Submits a file or URL in Simulated mode, waits for a terminal status, asserts the common invariants and returns the task.</summary>
    private async Task<RunResult> RunAsync(string? fileName, byte[]? file, string? url)
    {
        var f = NewFactory();
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
        var taskId = (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        AnalysisTask task;
        var deadline = DateTimeOffset.UtcNow + RunTimeout;
        while (true)
        {
            await using var db = Db(f);
            task = await db.Tasks.AsNoTracking().Include(t => t.StatusHistory).SingleAsync(t => t.Id == taskId, Ct);
            if (task.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled) break;
            Assert.True(DateTimeOffset.UtcNow < deadline, $"task {taskId} still {task.Status} after {RunTimeout}");
            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        }

        ReportBootTime(task);
        Assert.True(task.Status == RunStatus.Completed, $"task ended {task.Status}: {task.FailureReason}");

        await using (var db = Db(f))
        {
            // The isolation self-test passed before anything was delivered.
            Assert.Contains(await db.AuditLogs.AsNoTracking().Where(a => a.Target == taskId.ToString()).Select(a => a.Action).ToListAsync(Ct),
                a => a == "isolation.selftest.passed");
        }
        await AssertNothingLeftBehindAsync();
        return new RunResult(taskId, task, f);
    }

    private static MaldexDbContext Db(MaldexAppFactory f) =>
        new(new DbContextOptionsBuilder<MaldexDbContext>().UseSqlServer(f.ConnectionString).Options);

    /// <summary>Success criterion 1 is measured, not asserted, in dev: Preparing -> agent Ready (the Running transition).</summary>
    private void ReportBootTime(AnalysisTask task)
    {
        var preparing = task.StatusHistory.FirstOrDefault(h => h.To == RunStatus.Preparing);
        var running = task.StatusHistory.FirstOrDefault(h => h.To == RunStatus.Running);
        output.WriteLine(preparing is not null && running is not null
            ? $"Preparing -> agent Ready: {(running.At - preparing.At).TotalSeconds:0.0} s (task {task.Id})"
            : $"Preparing -> agent Ready: not measurable (task {task.Id} never reached Running)");
    }

    // ---- cleanup checks -----------------------------------------------------------------------------------------

    private static async Task<string> CaptureAsync(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(Ct);
        _ = p.StandardError.ReadToEndAsync(Ct);
        await p.WaitForExitAsync(Ct);
        return await stdout;
    }

    /// <summary>After a run: no maldex-* libvirt domain and no mx* link or network namespace may survive (teardown can lag briefly).</summary>
    private static async Task AssertNothingLeftBehindAsync()
    {
        List<string> domains = [], links = [], namespaces = [];
        for (var attempt = 0; attempt < 15; attempt++)
        {
            domains = (await CaptureAsync("virsh", "-c", "qemu:///session", "list", "--all", "--name"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(n => n.StartsWith("maldex-", StringComparison.Ordinal)).ToList();
            links = (await CaptureAsync("ip", "-o", "link", "show"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split(':', 3)[1].Trim().Split('@')[0]).Where(n => n.StartsWith("mx", StringComparison.Ordinal)).ToList();
            namespaces = (await CaptureAsync("ip", "netns", "list"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')[0]).Where(n => n.StartsWith("mx", StringComparison.Ordinal)).ToList();
            if (domains.Count + links.Count + namespaces.Count == 0) return;
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        }
        Assert.Fail($"left behind: domains [{string.Join(", ", domains)}], links [{string.Join(", ", links)}], namespaces [{string.Join(", ", namespaces)}]");
    }

    // ---- tests --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Eicar_IsMalicious()
    {
        RequireRealStack();
        var b64 = await File.ReadAllTextAsync(Path.Combine(SamplesDir, "eicar.txt.b64"), Ct);
        var r = await RunAsync("eicar.txt", Convert.FromBase64String(b64.Trim()), url: null);

        Assert.Equal(Verdict.Malicious, r.Task.Verdict);
        await using var db = Db(r.Factory);
        Assert.Contains(await db.Detections.AsNoTracking().Where(d => d.TaskId == r.TaskId).ToListAsync(Ct),
            d => d.Source == DetectionSource.Yara && d.Rule.Contains("EICAR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RegWriteScript_PersistenceHeuristic()
    {
        RequireRealStack();
        var script = await File.ReadAllBytesAsync(Path.Combine(SamplesDir, "regwrite.ps1"), Ct);
        var r = await RunAsync("regwrite.ps1", script, url: null);

        Assert.True(r.Task.Verdict >= Verdict.Suspicious, $"verdict {r.Task.Verdict}");
        await using var db = Db(r.Factory);
        Assert.Contains(await db.Detections.AsNoTracking().Where(d => d.TaskId == r.TaskId).ToListAsync(Ct),
            d => d.Source == DetectionSource.Heuristic && d.Rule == "persistence_run_key");
        Assert.Contains(await db.BehaviorEvents.AsNoTracking().Where(e => e.TaskId == r.TaskId && e.Type == EventType.Registry).ToListAsync(Ct),
            e => e.Data.Contains("MaldexTest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ControlledUrl_CapturesDnsFlowAndHttp()
    {
        RequireRealStack();
        var url = (await File.ReadAllTextAsync(Path.Combine(SamplesDir, "url.txt"), Ct)).Trim();
        var r = await RunAsync(null, null, url);

        await using var db = Db(r.Factory);
        var artifacts = await db.Artifacts.AsNoTracking().Where(a => a.TaskId == r.TaskId).Select(a => a.Kind).ToListAsync(Ct);
        Assert.Contains(ArtifactKind.Pcap, artifacts);
        Assert.Contains(ArtifactKind.MitmFlows, artifacts);

        Assert.Contains(await db.BehaviorEvents.AsNoTracking().Where(e => e.TaskId == r.TaskId && e.Type == EventType.Dns).ToListAsync(Ct),
            e => e.Summary.Contains("maldex-test.invalid", StringComparison.OrdinalIgnoreCase) || e.Data.Contains("maldex-test.invalid", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(await db.NetworkFlows.AsNoTracking().Where(f => f.TaskId == r.TaskId).ToListAsync(Ct));
        Assert.Contains(await db.HttpTransactions.AsNoTracking().Where(h => h.TaskId == r.TaskId).ToListAsync(Ct),
            h => h.Host.Contains("maldex-test.invalid", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(await db.Detections.AsNoTracking().Where(d => d.TaskId == r.TaskId && d.Source == DetectionSource.Suricata).ToListAsync(Ct),
            d => d.Details != null && d.Details.Contains("sid 9000001", StringComparison.Ordinal));
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
        var r = await RunAsync(Path.GetFileName(sample!), await File.ReadAllBytesAsync(sample!, Ct), url: null);

        Assert.True(r.Task.Verdict >= Verdict.Suspicious, $"verdict {r.Task.Verdict}");
        await using var db = Db(r.Factory);
        Assert.Contains(await db.Detections.AsNoTracking().Where(d => d.TaskId == r.TaskId).ToListAsync(Ct),
            d => d.Source == DetectionSource.Heuristic && d.Rule == "office_spawns_process");
    }
}

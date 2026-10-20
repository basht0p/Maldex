using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Execution;
using Maldex.Infrastructure.Net;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Core.Tests.Net;

public sealed class CaptureServiceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ScriptedRunner(string enablePath) : IProcessRunner
    {
        public readonly List<string> Calls = [];
        public bool EnabledWhenTcpdumpStarted;
        public bool TcpdumpCreatesFile = true;
        public int PcapBytes = 1000;
        public bool Alive;
        public bool IgnoreInt;
        public bool Impostor;
        private string _pcap = "";

        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null, TimeSpan? timeout = null)
        {
            var line = file + " " + string.Join(' ', args);
            lock (Calls) Calls.Add(line);
            if (line.Contains(" tcpdump "))
            {
                EnabledWhenTcpdumpStarted = File.Exists(enablePath);
                _pcap = args[^1];
                if (TcpdumpCreatesFile) File.WriteAllBytes(_pcap, new byte[PcapBytes]);
                Alive = true;
                return Ok();
            }
            if (line.StartsWith("sudo -n ip netns pids ")) return Ok(out_: "17 4242\n");
            if (line == "cat /proc/17/cmdline") return Ok(out_: "/opt/mitmproxy/mitmdump\0--mode\0transparent\0");
            if (line == "cat /proc/4242/cmdline")
                return Impostor ? Ok(out_: "sleep\01000\0") : Alive ? Ok(out_: $"tcpdump\0-n\0-i\0x\0-w\0{_pcap}\0") : Ok(1);
            if (line.StartsWith("sudo -n kill -INT")) { if (!IgnoreInt) Alive = false; return Ok(); }
            if (line.StartsWith("sudo -n kill -KILL")) { Alive = false; return Ok(); }
            return Ok();
        }

        private static Task<ProcessResult> Ok(int code = 0, string out_ = "") => Task.FromResult(new ProcessResult(code, out_, ""));
    }

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mx-cap-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _id = Guid.NewGuid();
    private string Ns => RunNetNames.For(_id).Namespace;
    private string Ctl => Path.Combine(_tmp, "mitm", "ctl");
    private string Enable => Path.Combine(Ctl, "capture.on");
    private string Flows => Path.Combine(_tmp, "mitm", "out", "flows.jsonl");
    private string CapDir => Path.Combine(_tmp, "run", "net", Ns, "cap");

    public CaptureServiceTests()
    {
        Directory.CreateDirectory(Ctl);
        Directory.CreateDirectory(Path.GetDirectoryName(Flows)!);
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    private CaptureService Service(ScriptedRunner r, Action<string>? warn = null, long maxPcap = 1 << 30) => new(
        Options.Create(new NetOptions { RunDir = Path.Combine(_tmp, "run") }), r, warn,
        new CaptureTuning
        {
            PollInterval = TimeSpan.FromMilliseconds(5), StartWait = TimeSpan.FromMilliseconds(100), StopWait = TimeSpan.FromMilliseconds(100),
            EnablePath = _ => Enable, FlowsPath = _ => Flows, MaxPcapBytes = maxPcap,
        });

    private NetworkLease Lease => new(_id, RunNetNames.For(_id).Tap);

    [Fact]
    public async Task Start_LaunchesTcpdumpInNamespace_ThenEnablesFlows()
    {
        var r = new ScriptedRunner(Enable);
        await using var handle = await Service(r).StartAsync(Lease, Ct);

        var tcpdump = Assert.Single(r.Calls, c => c.Contains(" tcpdump "));
        var names = RunNetNames.For(_id);
        Assert.Contains("sudo -n setsid -f sh -c", tcpdump);
        Assert.Contains($"ip netns exec {names.Namespace} ", tcpdump);
        Assert.Contains($" -i {names.VethNs} -U -s 0 ", tcpdump);
        Assert.Contains($" -Z {Environment.UserName} ", tcpdump);
        Assert.EndsWith(Path.Combine(CapDir, "capture.pcap"), tcpdump);
        Assert.False(r.EnabledWhenTcpdumpStarted, "flow logging must not be enabled before tcpdump is running");
        Assert.True(File.Exists(Enable), "capture.on must exist once capture is running");
    }

    [Fact]
    public async Task Start_TcpdumpNeverCreatesFile_ThrowsAndLeavesNothingRunning()
    {
        var r = new ScriptedRunner(Enable) { TcpdumpCreatesFile = false };
        await Assert.ThrowsAsync<CaptureStartException>(() => Service(r).StartAsync(Lease, Ct));
        Assert.False(File.Exists(Enable));
        Assert.False(r.Alive, "tcpdump must be stopped after a failed start");
    }

    [Fact]
    public async Task Stop_ReturnsPcapAndFlows_DisablesFlowsAndInterruptsTcpdump()
    {
        var r = new ScriptedRunner(Enable);
        await using var handle = await Service(r).StartAsync(Lease, Ct);
        File.WriteAllText(Flows, "{\"url\":\"http://203.0.113.5/\"}\n");

        var files = await handle.StopAsync(Ct);

        Assert.Equal([ArtifactKind.Pcap, ArtifactKind.MitmFlows], files.Select(f => f.Kind));
        Assert.Equal(Path.Combine(CapDir, "capture.pcap"), files[0].Path);
        Assert.Equal(Path.Combine(CapDir, "flows.jsonl"), files[1].Path);
        Assert.Equal(1000, new FileInfo(files[0].Path).Length);
        Assert.Contains("203.0.113.5", File.ReadAllText(files[1].Path));
        Assert.False(File.Exists(Enable));
        Assert.Contains(r.Calls, c => c == "sudo -n kill -INT 4242");
        Assert.DoesNotContain(r.Calls, c => c.Contains("kill -INT 17"));
        Assert.Equal(2, (await handle.StopAsync(Ct)).Count); // idempotent
    }

    [Fact]
    public async Task Stop_SkipsEmptyFiles()
    {
        var r = new ScriptedRunner(Enable) { PcapBytes = 24 }; // a pcap with only its global header holds no packets
        await using var handle = await Service(r).StartAsync(Lease, Ct);
        File.WriteAllText(Flows, "");
        Assert.Empty(await handle.StopAsync(Ct));
    }

    [Fact]
    public async Task Stop_NoFlowsFile_ReturnsPcapOnly()
    {
        var r = new ScriptedRunner(Enable);
        await using var handle = await Service(r).StartAsync(Lease, Ct);
        Assert.Equal(ArtifactKind.Pcap, Assert.Single(await handle.StopAsync(Ct)).Kind);
    }

    [Fact]
    public async Task Stop_FlowsIsSymlink_IsRefusedNotFollowed()
    {
        var secret = Path.Combine(_tmp, "secret.txt");
        File.WriteAllText(secret, "top secret");
        File.CreateSymbolicLink(Flows, secret);
        var warnings = new List<string>();
        var r = new ScriptedRunner(Enable);
        await using var handle = await Service(r, warnings.Add).StartAsync(Lease, Ct);
        Assert.Equal(ArtifactKind.Pcap, Assert.Single(await handle.StopAsync(Ct)).Kind);
        Assert.Single(warnings);
    }

    [Fact]
    public async Task Stop_TcpdumpIgnoresInterrupt_IsKilled()
    {
        var r = new ScriptedRunner(Enable) { IgnoreInt = true };
        await using var handle = await Service(r).StartAsync(Lease, Ct);
        await handle.StopAsync(Ct);
        Assert.Contains(r.Calls, c => c == "sudo -n kill -KILL 4242");
    }

    [Fact]
    public async Task Dispose_StopsAndDeletesCaptureFiles()
    {
        var r = new ScriptedRunner(Enable);
        var handle = await Service(r).StartAsync(Lease, Ct);
        await handle.DisposeAsync();
        Assert.False(r.Alive);
        Assert.False(Directory.Exists(CapDir));
        Assert.False(File.Exists(Enable));
    }

    [Fact]
    public async Task RunDirWithSpaces_PidStillMatched_AndPcapReturned()
    {
        var spaced = Path.Combine(_tmp, "run dir", "with  spaces");
        var r = new ScriptedRunner(Enable);
        var svc = new CaptureService(Options.Create(new NetOptions { RunDir = spaced }), r, null,
            new CaptureTuning { PollInterval = TimeSpan.FromMilliseconds(5), StartWait = TimeSpan.FromMilliseconds(100), StopWait = TimeSpan.FromMilliseconds(100), EnablePath = _ => Enable, FlowsPath = _ => Flows });
        await using var handle = await svc.StartAsync(Lease, Ct);
        var files = await handle.StopAsync(Ct);
        Assert.Contains(" ", Assert.Single(files).Path);
        Assert.Contains(r.Calls, c => c == "sudo -n kill -INT 4242");
    }

    [Fact]
    public async Task Stop_PidRecycledByAnotherProcess_NoSignalIsSent()
    {
        var r = new ScriptedRunner(Enable);
        await using var handle = await Service(r).StartAsync(Lease, Ct);
        r.Impostor = true; // tcpdump died early (e.g. SIGXFSZ) and the PID now belongs to something else
        await handle.StopAsync(Ct);
        Assert.DoesNotContain(r.Calls, c => c.StartsWith("sudo -n kill"));
    }

    [Fact]
    public async Task Stop_TcpdumpAlreadyGone_WarnsExitedEarly()
    {
        var r = new ScriptedRunner(Enable);
        await using var handle = await Service(r).StartAsync(Lease, Ct);
        r.Alive = false;
        await handle.StopAsync(Ct);
        Assert.Contains(handle.Warnings, w => w.Contains("exited early"));
        Assert.DoesNotContain(r.Calls, c => c.StartsWith("sudo -n kill"));
    }

    [Fact]
    public async Task Stop_PcapAtSizeLimit_WarnsTruncated()
    {
        var r = new ScriptedRunner(Enable) { PcapBytes = 1000 };
        await using var handle = await Service(r, maxPcap: 1000).StartAsync(Lease, Ct);
        r.Alive = false; // killed by SIGXFSZ at the limit
        var files = await handle.StopAsync(Ct);
        Assert.Equal(ArtifactKind.Pcap, Assert.Single(files).Kind);
        Assert.Equal(["pcap truncated at the size limit"], handle.Warnings);
    }

    [Fact]
    public async Task Stop_Healthy_HasNoWarnings()
    {
        var r = new ScriptedRunner(Enable);
        await using var handle = await Service(r).StartAsync(Lease, Ct);
        await handle.StopAsync(Ct);
        Assert.Empty(handle.Warnings);
    }
}

using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Execution;
using Maldex.Infrastructure.Net;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.Net;

/// <summary>
/// Gated: needs MALDEX_REAL_NET=1, passwordless sudo and the real-mode toolchain (nft, dnsmasq, mitmdump, inetsim, curl).
/// A scratch "guest stand-in" namespace is attached to the run bridge by a veth (playing the tap) to probe from the guest side.
/// Only interfaces/namespaces named mx* are created or removed.
/// </summary>
public class RealEgressTests
{
    private static readonly ProcessRunner Proc = new();

    [Fact]
    public Task CreateReleaseRealNamespace_None() => Exercise(NetworkMode.None);

    [Fact]
    public Task CreateReleaseRealNamespace_Simulated() => Exercise(NetworkMode.Simulated);

    private static async Task<ProcessResult> Sudo(params string[] args) => await Proc.RunAsync("sudo", ["-n", .. args], CancellationToken.None, null, TimeSpan.FromSeconds(30));

    private static string RepoDeployNet()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "deploy", "net", "inetsim"))) return Path.Combine(d.FullName, "deploy", "net");
        throw new InvalidOperationException("deploy/net not found above " + AppContext.BaseDirectory);
    }

    private static async Task Exercise(NetworkMode mode)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_NET") == "1" && OperatingSystem.IsLinux(),
            "set MALDEX_REAL_NET=1 (needs passwordless sudo, nft, dnsmasq, mitmdump, inetsim)");

        var hostIp = (await Proc.RunAsync("sh", ["-c", "ip -4 -o addr show scope global | grep -v ' mx' | head -n1 | awk '{print $4}' | cut -d/ -f1"], CancellationToken.None)).Stdout.Trim();
        Assert.False(string.IsNullOrEmpty(hostIp), "no host LAN IPv4 found");

        var tmp = Path.Combine(Path.GetTempPath(), "mx-real-net-" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new NetOptions
        {
            RunDir = Path.Combine(tmp, "run"),
            MitmConfDir = Path.Combine(tmp, "ca"),
            DeployNetDir = RepoDeployNet(),
            ControlPlaneIps = [hostIp],
        });
        // seed the CA the guest image would trust (mitmdump writes it at start-up, then idles; stop it)
        await Proc.RunAsync("timeout", ["8", "/opt/mitmproxy/mitmdump", "--set", $"confdir={Path.Combine(tmp, "ca")}", "-n", "-q"], CancellationToken.None, null, TimeSpan.FromSeconds(20));
        var router = new NetnsEgressRouter(opts, Proc);
        var id = Guid.NewGuid();
        var n = RunNetNames.For(id);
        var h = id.ToString("N")[..12];
        var scratchNs = "mxs" + h;
        var scratchHost = "mxq" + h;
        var scratchNsLeg = "mxs" + h;
        var subnet = RunSubnet.For(0);
        NetworkLease? lease = null;
        try
        {
            lease = await router.CreateAsync(id, mode, null, CancellationToken.None);
            Assert.Equal(n.Tap, lease.NetworkId);

            Assert.Contains(n.Namespace, (await Sudo("ip", "netns", "list")).Stdout);
            var rules = (await Sudo("ip", "netns", "exec", n.Namespace, "nft", "list", "ruleset")).Stdout;
            Assert.True(rules.Split("policy drop").Length - 1 >= 3, "expected policy drop on input/forward/output:\n" + rules);
            var addr = (await Sudo("ip", "-o", "addr", "show", n.Bridge)).Stdout;
            Assert.DoesNotContain(" inet ", addr);
            Assert.Contains(n.Bridge, (await Sudo("ip", "-o", "link", "show", n.Bridge)).Stdout);
            Assert.Contains(n.Tap, (await Sudo("ip", "-o", "link", "show", n.Tap)).Stdout);

            // mitmdump (hostile-traffic parser + our addon) runs as the dedicated powerless user, fully de-privileged
            var mitmUid = (await Proc.RunAsync("id", ["-u", "maldex-mitm"], CancellationToken.None)).Stdout.Trim();
            var mitmGid = (await Proc.RunAsync("id", ["-g", "maldex-mitm"], CancellationToken.None)).Stdout.Trim();
            var pids = (await Sudo("ip", "netns", "pids", n.Namespace)).Stdout.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
            string? mitmPid = null;
            foreach (var pid in pids)
                if ((await Proc.RunAsync("ps", ["-o", "comm=", "-p", pid], CancellationToken.None)).Stdout.Trim() == "mitmdump") mitmPid = pid;
            Assert.NotNull(mitmPid);
            var status = File.ReadAllLines($"/proc/{mitmPid}/status").ToDictionary(l => l.Split(':')[0], l => l.Split(':', 2)[1].Trim());
            Assert.Equal([mitmUid, mitmUid, mitmUid, mitmUid], status["Uid"].Split('\t'));
            Assert.Equal([mitmGid, mitmGid, mitmGid, mitmGid], status["Gid"].Split('\t'));
            Assert.Equal("", status["Groups"]);
            Assert.Equal("1", status["NoNewPrivs"]);
            Assert.Equal("0000000000000000", status["CapBnd"]);
            Assert.Equal("0000000000000000", status["CapEff"]);
            Assert.Equal("0000000000000000", status["CapInh"]);
            // the mitm user can read/write only its own conf/out (private CA copy 0600), nothing of ours
            Assert.Equal("maldex-mitm", (await Sudo("stat", "-c", "%U", $"/run/maldex/mitm/{n.Namespace}/conf")).Stdout.Trim());
            Assert.Equal("root", (await Sudo("stat", "-c", "%U", $"/run/maldex/mitm/{n.Namespace}")).Stdout.Trim());
            Assert.Equal("root", (await Sudo("stat", "-c", "%U", $"/run/maldex/mitm/{n.Namespace}/addon/maldex_jsonl.py")).Stdout.Trim());

            // guest stand-in: scratch namespace + veth into the run bridge (in place of the tap)
            Assert.Equal(0, (await Sudo("ip", "netns", "add", scratchNs)).ExitCode);
            Assert.Equal(0, (await Sudo("ip", "link", "add", scratchHost, "type", "veth", "peer", "name", scratchNsLeg, "netns", scratchNs)).ExitCode);
            Assert.Equal(0, (await Sudo("ip", "link", "set", scratchHost, "master", n.Bridge)).ExitCode);
            Assert.Equal(0, (await Sudo("ip", "link", "set", scratchHost, "up")).ExitCode);
            async Task<ProcessResult> Guest(params string[] a) => await Sudo(["ip", "netns", "exec", scratchNs, .. a]);
            await Guest("sysctl", "-w", "net.ipv6.conf.all.disable_ipv6=1");
            Assert.Equal(0, (await Guest("ip", "addr", "add", subnet.GuestIp + "/30", "dev", scratchNsLeg)).ExitCode);
            Assert.Equal(0, (await Guest("ip", "link", "set", scratchNsLeg, "up")).ExitCode);
            Assert.Equal(0, (await Guest("ip", "link", "set", "lo", "up")).ExitCode);
            Assert.Equal(0, (await Guest("ip", "route", "add", "default", "via", subnet.GatewayIp)).ExitCode);

            // sanity: the stand-in really is attached to the gateway (ARP resolves; ICMP to the gateway itself is dropped by design)
            await Guest("ping", "-c", "1", "-W", "2", subnet.GatewayIp);
            var neigh = (await Guest("ip", "neigh", "show", subnet.GatewayIp)).Stdout;
            Assert.Contains("lladdr", neigh);
            // ... but nothing else is reachable
            Assert.NotEqual(0, (await Guest("ping", "-c", "1", "-W", "2", hostIp)).ExitCode);
            Assert.NotEqual(0, (await Guest("ping", "-c", "1", "-W", "2", "8.8.8.8")).ExitCode);
            Assert.NotEqual(0, (await Guest("curl", "-s", "-m", "3", "-o", "/dev/null", $"http://{subnet.GatewayIp}:22/")).ExitCode);
            Assert.NotEqual(0, (await Guest("curl", "-s", "-m", "3", "-o", "/dev/null", $"http://{hostIp}:22/")).ExitCode);
            Assert.NotEqual(0, (await Guest("curl", "-s", "-m", "3", "-o", "/dev/null", $"http://{subnet.GatewayIp}:8080/")).ExitCode); // direct mitm port

            // capture end to end (Simulated): enable capture, make an HTTP request from the guest, expect a flows line
            if (mode == NetworkMode.Simulated) await File.WriteAllTextAsync(NetnsEgressRouter.MitmCaptureEnablePath(id), "");
            var any = await Guest("curl", "-s", "-m", "8", "-o", "/dev/null", "-w", "%{http_code}", "http://203.0.113.5/");
            if (mode == NetworkMode.Simulated)
            {
                Assert.Equal(0, any.ExitCode);
                Assert.Contains(any.Stdout.Trim(), new[] { "200", "404" }); // INetSim answers (its default root returns a 404 page here)
                var page = await Guest("curl", "-s", "-m", "8", "http://203.0.113.5/");
                Assert.Contains("html", page.Stdout, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.NotEqual(0, any.ExitCode);
            }

            if (mode == NetworkMode.Simulated)
            {
                var flowsPath = NetnsEgressRouter.MitmFlowsPath(id);
                string[] lines = [];
                for (var i = 0; i < 50 && lines.Length == 0; i++)
                {
                    if (File.Exists(flowsPath)) lines = File.ReadAllLines(flowsPath);
                    if (lines.Length == 0) await Task.Delay(100);
                }
                Assert.NotEmpty(lines);
                Assert.Contains("203.0.113.5", lines[0]);
                Assert.Equal("maldex-mitm 640", (await Sudo("stat", "-c", "%U %a", flowsPath)).Stdout.Trim());
                var dataDir = NetnsEgressRouter.InetSimDataPath(id);
                Assert.Equal(0, (await Sudo("test", "-d", dataDir + "/http/postdata")).ExitCode);
                Assert.Equal(0, (await Sudo("test", "-f", dataDir + "/http/mime.types")).ExitCode);
                Assert.Equal("inetsim", (await Sudo("stat", "-c", "%U", dataDir + "/http/postdata")).Stdout.Trim());
                // nothing under the run's inetsim tree is owned by (or writable for) inetsim except the leaves and the log dir
                var iRoot = Path.GetDirectoryName(dataDir)!;
                var owned = (await Sudo("find", iRoot, "-user", "inetsim", "-type", "d")).Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x).ToArray();
                Assert.Equal(new[] { dataDir + "/ftp/upload", dataDir + "/http/postdata", dataDir + "/smtp" }.OrderBy(x => x), owned);
                // log/ is root:inetsim 0750 (inetsim cannot create entries); reports go to a separate root dir; no inetsim-owned file is in either
                Assert.Equal("root:inetsim 750", (await Sudo("stat", "-c", "%U:%G %a", iRoot + "/log")).Stdout.Trim());
                Assert.Equal("root:root 755", (await Sudo("stat", "-c", "%U:%G %a", iRoot + "/report")).Stdout.Trim());
                Assert.Equal("", (await Sudo("find", iRoot + "/log", iRoot + "/report", "-user", "inetsim")).Stdout.Trim());
                Assert.Equal("", (await Sudo("find", iRoot, "-type", "l")).Stdout.Trim());
                Assert.Equal("root", (await Sudo("stat", "-c", "%U", iRoot)).Stdout.Trim());
                Assert.Equal("root", (await Sudo("stat", "-c", "%U", dataDir)).Stdout.Trim());
            }
            Assert.Contains(lease, await router.ListAsync(CancellationToken.None));
            if (mode == NetworkMode.Simulated)
            {
                // release keeps the flows for collection (safe copy), then everything is torn down
                await router.ReleaseAsync(lease, CancellationToken.None);
                var kept = Path.Combine(tmp, "run", "net", n.Namespace, "flows.jsonl");
                Assert.True(File.Exists(kept) && new FileInfo(kept).Length > 0, "flows.jsonl must be kept in the run net dir");
            }
        }
        finally
        {
            if (lease is not null) await router.ReleaseAsync(lease, CancellationToken.None);
            else await router.ReleaseAsync(new NetworkLease(id, n.Tap), CancellationToken.None);
            await router.ReleaseAsync(new NetworkLease(id, n.Tap), CancellationToken.None); // idempotent
            await Sudo("ip", "link", "del", scratchHost);
            await Sudo("ip", "netns", "del", scratchNs);
            await Sudo("rm", "-rf", tmp);
        }

        Assert.DoesNotContain(h, (await Proc.RunAsync("ip", ["-o", "link"], CancellationToken.None)).Stdout);
        Assert.False(Directory.Exists("/run/maldex/mitm/mxn" + h), "per-run mitm dir must be removed");
        Assert.False(Directory.Exists("/run/maldex/inetsim/mxn" + h), "per-run inetsim dir must be removed");
        Assert.DoesNotContain(h, (await Proc.RunAsync("ip", ["netns", "list"], CancellationToken.None)).Stdout);
    }
}

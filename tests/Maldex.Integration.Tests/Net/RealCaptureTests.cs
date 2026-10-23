using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Execution;
using Maldex.Infrastructure.Net;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.Net;

/// <summary>
/// Gated: needs MALDEX_REAL_NET=1, passwordless sudo and the real-mode toolchain (nft, dnsmasq, mitmdump, inetsim, curl, tcpdump).
/// A scratch "guest stand-in" namespace is attached to the run bridge by a veth (playing the tap). Only mx* objects are touched.
/// </summary>
public class RealCaptureTests
{
    private static readonly ProcessRunner Proc = new();

    [Fact]
    public async Task CaptureGuestStandInPing()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_NET") == "1" && OperatingSystem.IsLinux(),
            "set MALDEX_REAL_NET=1 (needs passwordless sudo, nft, dnsmasq, mitmdump, inetsim, tcpdump)");

        var tmp = Path.Combine(Path.GetTempPath(), "mx-real-cap-" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new NetOptions
        {
            RunDir = Path.Combine(tmp, "run"),
            MitmConfDir = Path.Combine(tmp, "ca"),
            DeployNetDir = RepoDeployNet(),
            ControlPlaneIps = ["192.0.2.1"],
        });
        await Proc.RunAsync("timeout", ["8", "/opt/mitmproxy/mitmdump", "--set", $"confdir={Path.Combine(tmp, "ca")}", "-n", "-q"], CancellationToken.None, null, TimeSpan.FromSeconds(20));
        var warnings = new List<string>();
        var router = new NetnsEgressRouter(opts, Proc, warnings.Add);
        var capture = new CaptureService(opts, Proc, warnings.Add);
        var id = Guid.NewGuid();
        var n = RunNetNames.For(id);
        var h = id.ToString("N")[..12];
        var scratchNs = "mxs" + h;
        var scratchHost = "mxq" + h;
        NetworkLease? lease = null;
        ICaptureHandle? handle = null;
        try
        {
            lease = await router.CreateAsync(id, NetworkMode.Simulated, null, CancellationToken.None);
            var index = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(tmp, "run", "net", n.Namespace, "lease.json"), TestContext.Current.CancellationToken)).RootElement.GetProperty("Index").GetInt32();
            var subnet = RunSubnet.For(index);

            // guest stand-in: scratch namespace + veth into the run bridge (in place of the tap)
            Assert.Equal(0, (await Sudo("ip", "netns", "add", scratchNs)).ExitCode);
            Assert.Equal(0, (await Sudo("ip", "link", "add", scratchHost, "type", "veth", "peer", "name", scratchNs, "netns", scratchNs)).ExitCode);
            Assert.Equal(0, (await Sudo("ip", "link", "set", scratchHost, "master", n.Bridge)).ExitCode);
            Assert.Equal(0, (await Sudo("ip", "link", "set", scratchHost, "up")).ExitCode);
            async Task<ProcessResult> Guest(params string[] a) => await Sudo(["ip", "netns", "exec", scratchNs, .. a]);
            await Guest("sysctl", "-w", "net.ipv6.conf.all.disable_ipv6=1");
            Assert.Equal(0, (await Guest("ip", "addr", "add", subnet.GuestIp + "/30", "dev", scratchNs)).ExitCode);
            Assert.Equal(0, (await Guest("ip", "link", "set", scratchNs, "up")).ExitCode);
            Assert.Equal(0, (await Guest("ip", "link", "set", "lo", "up")).ExitCode);
            Assert.Equal(0, (await Guest("ip", "route", "add", "default", "via", subnet.GatewayIp)).ExitCode);

            handle = await capture.StartAsync(lease, CancellationToken.None);

            // the pcap was created by tcpdump after it dropped to the control-plane user, in a 0700 directory
            var capDir = Path.Combine(tmp, "run", "net", n.Namespace, "cap");
            Assert.Equal($"{Environment.UserName} 700", (await Proc.RunAsync("stat", ["-c", "%U %a", capDir], CancellationToken.None)).Stdout.Trim());
            Assert.Equal(Environment.UserName, (await Proc.RunAsync("stat", ["-c", "%U", Path.Combine(capDir, "capture.pcap")], CancellationToken.None)).Stdout.Trim());
            Assert.True(File.Exists(NetnsEgressRouter.MitmCaptureEnablePath(id)), "flow logging must be enabled");

            var curl = await Guest("curl", "-s", "-m", "8", "-o", "/dev/null", "-w", "%{http_code}", "http://203.0.113.5/");
            Assert.Equal(0, curl.ExitCode);
            Assert.Contains(curl.Stdout.Trim(), new[] { "200", "404" });

            // the flow line is written when the response completes; give the proxy a moment before stopping
            for (var i = 0; i < 50 && !(File.Exists(NetnsEgressRouter.MitmFlowsPath(id)) && new FileInfo(NetnsEgressRouter.MitmFlowsPath(id)).Length > 0); i++)
                await Task.Delay(100, TestContext.Current.CancellationToken);

            var files = await handle.StopAsync(CancellationToken.None);

            var pcap = Assert.Single(files, f => f.Kind == ArtifactKind.Pcap);
            Assert.True(new FileInfo(pcap.Path).Length > 24, "pcap holds packets");
            var dump = (await Proc.RunAsync("tcpdump", ["-nr", pcap.Path], CancellationToken.None)).Stdout;
            Assert.Contains("203.0.113.5", dump);
            Assert.Contains(subnet.GuestIp, dump);

            var flows = Assert.Single(files, f => f.Kind == ArtifactKind.MitmFlows);
            var lines = await File.ReadAllLinesAsync(flows.Path, TestContext.Current.CancellationToken);
            Assert.NotEmpty(lines);
            Assert.Contains("203.0.113.5", lines[0]);
            Assert.False(File.Exists(NetnsEgressRouter.MitmCaptureEnablePath(id)), "flow logging is disabled after stop");

            // tcpdump is gone (only the router's daemons remain in the namespace)
            foreach (var pid in (await Sudo("ip", "netns", "pids", n.Namespace)).Stdout.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
                Assert.NotEqual("tcpdump", (await Proc.RunAsync("ps", ["-o", "comm=", "-p", pid], CancellationToken.None)).Stdout.Trim());

            await handle.DisposeAsync();
            handle = null;
            Assert.False(Directory.Exists(capDir), "dispose deletes the capture files");

            await router.ReleaseAsync(lease, CancellationToken.None);
            Assert.Empty(warnings);
        }
        finally
        {
            if (handle is not null) await handle.DisposeAsync();
            await router.ReleaseAsync(lease ?? new NetworkLease(id, n.Tap), CancellationToken.None);
            await Sudo("ip", "link", "del", scratchHost);
            await Sudo("ip", "netns", "del", scratchNs);
            await Sudo("rm", "-rf", tmp);
        }

        Assert.DoesNotContain(h, (await Proc.RunAsync("ip", ["-o", "link"], CancellationToken.None)).Stdout);
        Assert.DoesNotContain(h, (await Proc.RunAsync("ip", ["netns", "list"], CancellationToken.None)).Stdout);
        Assert.False(Directory.Exists("/run/maldex/mitm/mxn" + h));
        Assert.False(Directory.Exists("/run/maldex/inetsim/mxn" + h));
    }

    private static async Task<ProcessResult> Sudo(params string[] args) => await Proc.RunAsync("sudo", ["-n", .. args], CancellationToken.None, null, TimeSpan.FromSeconds(30));

    private static string RepoDeployNet()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "deploy", "net", "inetsim"))) return Path.Combine(d.FullName, "deploy", "net");
        throw new InvalidOperationException("deploy/net not found above " + AppContext.BaseDirectory);
    }
}

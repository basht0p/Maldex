using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Execution;
using Maldex.Infrastructure.Net;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Core.Tests.Net;

public sealed class NetnsEgressRouterTests : IDisposable
{
    /// <summary>Records every command; keeps a tiny model of which namespaces exist; fails commands matching a predicate.</summary>
    private sealed class ScriptedRunner : IProcessRunner
    {
        public readonly List<string> Calls = [];
        public readonly List<string?> Stdins = [];
        public readonly HashSet<string> Namespaces = [];
        public Func<string, bool> Fail = _ => false;
        public string Handshakes = "pubkey\t1700000000\n";
        public string Pids = "";
        public bool GuardPresent = true;
        /// <summary>Which of Docker's DOCKER-USER exception rules (<c>iptables -C</c>) exist: out = [0], replies back = [1].</summary>
        public bool[] DockerRules = [true, true];
        /// <summary>When set, the DOCKER-USER rules exist only for this interface.</summary>
        public string? DockerRulesIface;
        public string TemplateOffender = "";
        public bool HonorCancellation;
        public string? MissingUser;
        public string DefaultRoute = "default via 192.168.1.1 dev eth0 proto dhcp src 192.168.1.5 metric 100\n";
        public string NftTables = "";

        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null, TimeSpan? timeout = null)
        {
            var line = file + " " + string.Join(' ', args);
            lock (Calls) { Calls.Add(line); Stdins.Add(stdin); }
            if (HonorCancellation) ct.ThrowIfCancellationRequested();
            if (Fail(line)) return Task.FromResult(new ProcessResult(1, "", "scripted failure"));
            if (line.Contains("nft list table inet maldex_guard")) return Ok(GuardPresent ? 0 : 1);
            if (line.StartsWith("sudo -n ip netns add ")) { lock (Namespaces) Namespaces.Add(args[^1]); return Ok(); }
            if (line.StartsWith("sudo -n ip netns del ")) { lock (Namespaces) Namespaces.Remove(args[^1]); return Ok(); }
            if (line == "sudo -n ip netns list") { lock (Namespaces) return Ok(out_: string.Join('\n', Namespaces.Select(n => n + " (id: 0)"))); }
            if (line.StartsWith("sudo -n ip netns pids ")) return Ok(out_: Pids);
            if (line.Contains(" ss -ltn")) return Ok(out_: string.Join('\n', Enumerable.Range(0, 20).Select(i => $"LISTEN 0 128 10.213.{i / 64}.{i % 64 * 4 + 1}:8080 0.0.0.0:*")));
            if (line == "ip -4 -o route show default") return Ok(out_: DefaultRoute);
            if (line.StartsWith("sudo -n iptables -C DOCKER-USER -i mxu+ -o ")) return Ok(DockerRules[0] && (DockerRulesIface is null || line.Contains($" -o {DockerRulesIface} ")) ? 0 : 1);
            if (line.StartsWith("sudo -n iptables -C DOCKER-USER -i ") && line.Contains(" -o mxu+ ")) return Ok(DockerRules[1] && (DockerRulesIface is null || line.Contains($" -i {DockerRulesIface} ")) ? 0 : 1);
            if (line == "sudo -n nft list tables") return Ok(out_: NftTables);
            if (line == "id -u" || line == "id -g") return Ok(out_: "1000\n");
            if (line == "id -u maldex-mitm") return Ok(out_: "995\n");
            if (MissingUser is not null && line.EndsWith(" " + MissingUser)) return Ok(1);
            if (line == "id -u maldex-dns") return Ok(out_: "990\n");
            if (line == "id -u maldex-isroot") return Ok(out_: "994\n");
            if (line == "id -g maldex-isroot") return Ok(out_: "987\n");
            if (line == "id -u maldex-isuser") return Ok(out_: "993\n");
            if (line == "id -g maldex-isuser") return Ok(out_: "986\n");
            if (line == "id -u inetsim") return Ok(out_: "110\n");
            if (line == "id -g inetsim") return Ok(out_: "112\n");
            if (line.Contains(" find ") && line.Contains("-type l")) return Ok(out_: TemplateOffender);
            if (line.Contains(" curl ")) return Ok(out_: "200");
            if (line.Contains("latest-handshakes")) return Ok(out_: Handshakes);
            return Ok();
        }

        private static Task<ProcessResult> Ok(int code = 0, string out_ = "") => Task.FromResult(new ProcessResult(code, out_, ""));
        public int IndexOf(Func<string, bool> p) => Calls.FindIndex(c => p(c));
        public IEnumerable<string> Where(Func<string, bool> p) => Calls.Where(p);
    }

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mx-nsr-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedRunner _r = new();

    public NetnsEgressRouterTests()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "ca"));
        File.WriteAllText(ResolvConf, "# upstream\nnameserver 192.168.3.10\nnameserver 8.8.8.8\nnameserver 2001:4860:4860::8888\nsearch example.test\n");
        File.WriteAllText(IpForward, "1\n");
        foreach (var f in new[] { "mitmproxy-ca.pem", "mitmproxy-ca-cert.pem", "mitmproxy-dhparam.pem" })
            File.WriteAllText(Path.Combine(_tmp, "ca", f), "x");
    }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    private string ResolvConf => Path.Combine(_tmp, "resolv.conf");
    private string IpForward => Path.Combine(_tmp, "ip_forward");

    private NetnsEgressRouter Router(Action<string>? warn = null, IProcessRunner? runner = null, string[]? dns = null) => new(Options.Create(new NetOptions
    {
        DirectDnsServers = dns ?? [],
        RunDir = Path.Combine(_tmp, "run"),
        WireGuardConfigDir = Path.Combine(_tmp, "wg"),
        MitmConfDir = Path.Combine(_tmp, "ca"),
        DeployNetDir = Path.Combine(_tmp, "deploy"),
        ControlPlaneIps = ["192.168.50.7"],
    }), runner ?? _r, warn, new NetnsRouterTuning
    {
        PollInterval = TimeSpan.FromMilliseconds(5), MitmWait = TimeSpan.FromMilliseconds(100), InetSimWait = TimeSpan.FromMilliseconds(100),
        HandshakeWait = TimeSpan.FromMilliseconds(100), KillGrace = TimeSpan.FromMilliseconds(50),
        BrNetfilterPath = Path.Combine(_tmp, "no-br-netfilter"),
        ResolvConfPath = ResolvConf,
        IpForwardPath = IpForward,
    });

    private void WriteWg(string country, string body)
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "wg"));
        File.WriteAllText(Path.Combine(_tmp, "wg", country + ".conf"), body);
    }

    private const string GoodWg = """
        # MaldexEgressIp = 203.0.113.77
        [Interface]
        PrivateKey = cHJpdmF0ZQ==
        Address = 10.2.0.2/32
        DNS = 10.2.0.1
        MTU = 1380
        PostUp = echo hi
        [Peer]
        PublicKey = cHVibGlj
        AllowedIPs = 0.0.0.0/0
        Endpoint = 198.51.100.1:51820
        """;

    private static bool IsLinkUp(string c) => c.Contains(" ip link set ") && c.EndsWith(" up", StringComparison.Ordinal);
    private static bool IsNftApply(string c) => c.Contains(" nft -f -");

    // ---- Review Focus 1 ----

    [Theory]
    [InlineData(NetworkMode.None, " dnsmasq ")]
    [InlineData(NetworkMode.None, "mitmdump --mode")]
    [InlineData(NetworkMode.Simulated, "inetsim")]
    [InlineData(NetworkMode.Simulated, "route add local")]
    public async Task CreateFailsAfterBridge_LeavesNoRoute(NetworkMode mode, string failing)
    {
        var id = Guid.NewGuid();
        var n = RunNetNames.For(id);
        _r.Fail = c => c.Contains(failing) && !c.Contains("ip netns del");
        await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(id, mode, null, CancellationToken.None));

        // ruleset strictly before any link exists or is up
        var nft = _r.IndexOf(IsNftApply);
        Assert.True(nft >= 0);
        Assert.True(nft < _r.IndexOf(c => c.Contains("ip link add")));
        Assert.True(nft < _r.IndexOf(IsLinkUp));
        // teardown ran, and nothing is left
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip link del {n.Bridge}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip netns del {n.Namespace}");
        Assert.Empty(_r.Namespaces);
    }

    [Fact]
    public async Task CreateFailure_OriginalMessageIsCarried()
    {
        _r.Fail = c => c.Contains("ip tuntap add");
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None));
        Assert.Contains("scripted failure", ex.Message);
    }

    [Theory]
    [InlineData(NetworkMode.None)]
    [InlineData(NetworkMode.Simulated)]
    public async Task Ruleset_AppliedBeforeLinksUp(NetworkMode mode)
    {
        var id = Guid.NewGuid();
        await Router().CreateAsync(id, mode, null, CancellationToken.None);
        var nft = _r.IndexOf(IsNftApply);
        Assert.True(nft >= 0 && nft < _r.IndexOf(c => c.Contains("ip link add")));
        Assert.True(nft < _r.IndexOf(IsLinkUp));
        var stdin = _r.Stdins[nft];
        Assert.Contains("policy drop", stdin);
        Assert.Contains(RunNetNames.For(id).VethNs, stdin);
    }

    // ---- VPN ----

    [Fact]
    public async Task Vpn_NoHandshake_FailsClosed()
    {
        WriteWg("nl", GoodWg);
        _r.Handshakes = "pubkey\t0\n";
        var id = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(id, NetworkMode.LiveVpn, "nl", CancellationToken.None));
        Assert.Contains("handshake", ex.Message);
        Assert.Empty(_r.Namespaces);
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip link del {RunNetNames.For(id).Bridge}");
    }

    [Fact]
    public async Task Vpn_MissingConfig_FailsClosed()
    {
        await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.LiveVpn, "nl", CancellationToken.None));
        Assert.DoesNotContain(_r.Calls, c => c.Contains("ip netns add"));
    }

    [Fact]
    public async Task Vpn_MissingDns_FailsClosed()
    {
        WriteWg("nl", GoodWg.Replace("DNS = 10.2.0.1", ""));
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.LiveVpn, "nl", CancellationToken.None));
        Assert.Contains("DNS", ex.Message);
        Assert.DoesNotContain(_r.Calls, c => c.Contains("ip netns add"));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("nl/../../x")]
    [InlineData("")]
    public async Task Vpn_BadCountryName_Rejected(string country)
    {
        await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.LiveVpn, country, CancellationToken.None));
    }

    [Fact]
    public async Task Vpn_CountryBusy_FailsClosed()
    {
        WriteWg("nl", GoodWg);
        var router = Router();
        await router.CreateAsync(Guid.NewGuid(), NetworkMode.LiveVpn, "nl", CancellationToken.None);
        var second = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => router.CreateAsync(second, NetworkMode.LiveVpn, "nl", CancellationToken.None));
        Assert.Contains("busy", ex.Message);
        Assert.DoesNotContain(_r.Calls, c => c.Contains("ip netns add " + RunNetNames.For(second).Namespace));
    }

    [Fact]
    public async Task Vpn_HappyPath_UsesStrippedConfAndConfDnsEverywhere()
    {
        WriteWg("nl", GoodWg);
        var id = Guid.NewGuid();
        var n = RunNetNames.For(id);
        await Router().CreateAsync(id, NetworkMode.LiveVpn, "nl", CancellationToken.None);

        Assert.Contains(_r.Calls, c => c == $"sudo -n ip link add {n.WgIface} type wireguard");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip link set {n.WgIface} netns {n.Namespace}");
        Assert.Contains(_r.Calls, c => c.Contains($"ip netns exec {n.Namespace} ip addr add 10.2.0.2/32 dev {n.WgIface}"));
        Assert.Contains(_r.Calls, c => c.Contains($"ip netns exec {n.Namespace} ip route add default dev {n.WgIface}"));
        Assert.Contains(_r.Calls, c => c.Contains("--server=10.2.0.1"));
        // the wg-quick-only keys never reach `wg setconf` (the temp conf is deleted afterwards; check via the parser too)
        var parsed = WireGuardConf.Parse(GoodWg);
        Assert.DoesNotContain("Address", parsed.SetConfText);
        Assert.DoesNotContain("PostUp", parsed.SetConfText);
        Assert.Contains("PrivateKey", parsed.SetConfText);
        Assert.Equal("203.0.113.77", parsed.EgressIp);
        Assert.Equal("10.2.0.1", parsed.Dns);
        // VPN DNS reaches the ruleset
        Assert.Contains("ip daddr 10.2.0.1 udp dport 53", _r.Stdins[_r.IndexOf(IsNftApply)]);
    }

    [Fact]
    public async Task Vpn_ReleaseFreesTheCountry()
    {
        WriteWg("nl", GoodWg);
        var router = Router();
        var lease = await router.CreateAsync(Guid.NewGuid(), NetworkMode.LiveVpn, "nl", CancellationToken.None);
        await router.ReleaseAsync(lease, CancellationToken.None);
        await router.CreateAsync(Guid.NewGuid(), NetworkMode.LiveVpn, "nl", CancellationToken.None);
    }

    [Fact]
    public async Task LiveTor_Rejected()
    {
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.LiveTor, null, CancellationToken.None));
        Assert.Equal("LiveTor is not available", ex.Message);
        Assert.Empty(_r.Calls);
    }

    // ---- exits ----

    [Fact]
    public async Task None_Succeeds_NoExitStarted()
    {
        var id = Guid.NewGuid();
        var n = RunNetNames.For(id);
        var lease = await Router().CreateAsync(id, NetworkMode.None, null, CancellationToken.None);
        Assert.Equal(new NetworkLease(id, n.Tap), lease);
        Assert.DoesNotContain(_r.Calls, c => c.Contains("inetsim"));
        Assert.DoesNotContain(_r.Calls, c => c.Contains("route add"));
        Assert.DoesNotContain(_r.Calls, c => c.Contains("wireguard"));
        var dns = Assert.Single(_r.Where(c => c.Contains(" dnsmasq ")));
        Assert.Contains("--no-resolv", dns);
        // per-run lease file and a reservation for the run's deterministic guest MAC (no shared /var/lib/misc lease db)
        Assert.Contains($"--dhcp-leasefile=/run/maldex/dns/{n.Namespace}/dnsmasq.leases", dns);
        Assert.DoesNotContain(Path.Combine(_tmp, "run"), dns); // nothing of dnsmasq's lives in the 0700 run net dir
        Assert.Contains($"--pid-file=/run/maldex/dns/{n.Namespace}/dnsmasq.pid", dns);
        Assert.Contains($"--log-facility=/run/maldex/dns/{n.Namespace}/dnsmasq.log", dns);
        Assert.Contains("--dhcp-broadcast", dns); // no ARP-cache injection (needs CAP_NET_ADMIN)
        Assert.Contains("--no-ping", dns); // no raw ICMP socket (needs CAP_NET_RAW)
        Assert.Equal($"/run/maldex/dns/{n.Namespace}", NetnsEgressRouter.DnsDir(id));
        Assert.Contains($"--dhcp-host={RunNetNames.GuestMac(id)},10.213.0.2", dns);
        Assert.DoesNotContain("--server", dns);
        Assert.DoesNotContain("--address", dns);
        Assert.True(File.Exists(Path.Combine(_tmp, "run", "net", n.Namespace, "lease.json")));
    }

    [Theory]
    [InlineData(NetworkMode.None)]
    [InlineData(NetworkMode.Simulated)]
    [InlineData(NetworkMode.LiveVpn)]
    public async Task Dnsmasq_RunsWithNoCapabilities_ViaSetpriv_AndLowPortsAreOpenedFirst(NetworkMode mode)
    {
        if (mode == NetworkMode.LiveVpn) WriteWg("nl", GoodWg);
        var id = Guid.NewGuid();
        var n = RunNetNames.For(id);
        await Router().CreateAsync(id, mode, mode == NetworkMode.LiveVpn ? "nl" : null, CancellationToken.None);
        var dns = Assert.Single(_r.Where(c => c.Contains(" dnsmasq ")));
        Assert.StartsWith($"sudo -n ip netns exec {n.Namespace} setpriv --reuid=maldex-dns --regid=maldex-dns --clear-groups --no-new-privs --inh-caps=-all --bounding-set=-all -- dnsmasq ", dns);
        var ports = $"sudo -n ip netns exec {n.Namespace} sysctl -w net.ipv4.ip_unprivileged_port_start=0";
        Assert.Single(_r.Calls, c => c == ports); // a single place sets it, for every mode
        Assert.True(_r.IndexOf(c => c == ports) < _r.IndexOf(c => c.Contains(" dnsmasq ")));
        // staged like the mitm dirs: root-owned parents, a 0700 leaf owned by the dnsmasq user
        var d = $"/run/maldex/dns/{n.Namespace}";
        var parents = _r.IndexOf(c => c == "sudo -n install -d -m 0755 -o root -g root /run/maldex /run/maldex/dns");
        var leaf = _r.IndexOf(c => c == $"sudo -n install -d -m 0700 -o maldex-dns -g maldex-dns {d}");
        Assert.True(parents >= 0 && leaf > parents && leaf < _r.IndexOf(c => c.Contains(" dnsmasq ")));
    }

    [Fact]
    public async Task Dnsmasq_MissingUser_FailsClosed_BeforeStarting()
    {
        _r.MissingUser = "maldex-dns";
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None));
        Assert.Contains("maldex-dns", ex.Message);
        Assert.DoesNotContain(_r.Calls, c => c.Contains(" dnsmasq "));
        Assert.DoesNotContain(_r.Calls, c => c.Contains("install") && c.Contains("/run/maldex/dns"));
        Assert.Empty(_r.Namespaces);
    }

    [Fact]
    public async Task Dnsmasq_DirIsRemovedOnRelease_AndOnFailedCreate()
    {
        var id = Guid.NewGuid();
        var n = RunNetNames.For(id);
        var router = Router();
        var lease = await router.CreateAsync(id, NetworkMode.None, null, CancellationToken.None);
        await router.ReleaseAsync(lease, CancellationToken.None);
        Assert.Contains(_r.Calls, c => c == $"sudo -n rm -rf -- /run/maldex/dns/{n.Namespace}");
        _r.Calls.Clear();
        var id2 = Guid.NewGuid();
        _r.Fail = c => c.Contains("mitmdump --mode");
        await Assert.ThrowsAsync<EgressUnavailableException>(() => router.CreateAsync(id2, NetworkMode.None, null, CancellationToken.None));
        Assert.Contains(_r.Calls, c => c == $"sudo -n rm -rf -- /run/maldex/dns/{RunNetNames.For(id2).Namespace}");
    }

    [Fact]
    public void InetSimLauncher_StaysAsInit_ForwardingSignals_AndReaping()
    {
        var l = NetnsEgressRouter.UserNsLauncherScript;
        Assert.DoesNotContain("os._exit(0)", l); // PID 1 must not exit after the handshake
        Assert.Contains("signal.SIGTERM", l);
        Assert.Contains("signal.SIGINT", l);
        Assert.Contains("signal.SIGHUP", l);
        Assert.Contains("os.waitpid(-1, 0)", l);
        Assert.True(l.IndexOf("os.write(w, b\"1\")", StringComparison.Ordinal) < l.IndexOf("os.waitpid(-1, 0)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Simulated_StartsInetSim_AndAnyIpRoute()
    {
        var id = Guid.NewGuid();
        var n = RunNetNames.For(id);
        await Router().CreateAsync(id, NetworkMode.Simulated, null, CancellationToken.None);
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip netns exec {n.Namespace} ip route add local 0.0.0.0/0 dev lo table main");
        var inet = Assert.Single(_r.Where(c => c.Contains("inetsim ") && c.Contains("--config")));
        Assert.Contains("--bind-address 0.0.0.0", inet);
        Assert.Contains("setsid -f", inet);
        Assert.Contains(_r.Calls, c => c.Contains("--address=/#/198.18.0.10"));
        Assert.True(_r.IndexOf(c => c.Contains("route add local")) < _r.IndexOf(c => c.Contains("inetsim ")));
    }

    [Fact]
    public async Task Mitm_RunsUnprivileged_ViaSetpriv_InsideTheNamespace()
    {
        var id = Guid.NewGuid();
        await Router().CreateAsync(id, NetworkMode.None, null, CancellationToken.None);
        var m = Assert.Single(_r.Where(c => c.Contains("mitmdump --mode transparent")));
        Assert.Contains($"ip netns exec {RunNetNames.For(id).Namespace} setpriv --reuid=maldex-mitm --regid=maldex-mitm --clear-groups --no-new-privs --inh-caps=-all --bounding-set=-all -- ", m);
        Assert.True(m.IndexOf("setpriv", StringComparison.Ordinal) < m.IndexOf("mitmdump --mode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mitm_UidLookupFailure_FailsClosed()
    {
        _r.Fail = c => c == "id -u maldex-mitm";
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None));
        Assert.Contains("maldex-mitm", ex.Message);
        Assert.DoesNotContain(_r.Calls, c => c.Contains("mitmdump --mode"));
        Assert.Empty(_r.Namespaces);
    }

    [Fact]
    public async Task Mitm_InputsStagedByRoot_InRootOwnedDirs_WithPrivateCaCopy()
    {
        var id = Guid.NewGuid();
        var n = RunNetNames.For(id);
        var d = $"/run/maldex/mitm/{n.Namespace}";
        await Router().CreateAsync(id, NetworkMode.None, null, CancellationToken.None);
        Assert.Contains(_r.Calls, c => c.StartsWith("sudo -n install -d -m 0755 -o root -g root /run/maldex /run/maldex/mitm " + d));
        Assert.Contains(_r.Calls, c => c == $"sudo -n install -d -m 0700 -o maldex-mitm -g maldex-mitm {d}/conf");
        Assert.Contains(_r.Calls, c => c == $"sudo -n install -m 0600 -o maldex-mitm -g maldex-mitm {Path.Combine(_tmp, "ca", "mitmproxy-ca.pem")} {d}/conf/mitmproxy-ca.pem");
        Assert.Contains(_r.Calls, c => c == $"sudo -n install -d -m 2750 -o maldex-mitm -g 1000 {d}/out");
        Assert.Contains(_r.Calls, c => c == $"sudo -n install -d -m 0755 -o 1000 -g 1000 {d}/ctl");
        var m = Assert.Single(_r.Where(c => c.Contains("mitmdump --mode transparent")));
        Assert.Contains($"--set confdir={d}/conf", m);
        Assert.Contains($"-s {d}/addon/maldex_jsonl.py", m);
        Assert.Contains($"maldex_out={d}/out/flows.jsonl", m);
        Assert.Contains($"maldex_enable_file={d}/ctl/capture.on", m);
        // staged before it is launched
        Assert.True(_r.IndexOf(c => c.Contains(d + "/conf/mitmproxy-ca.pem")) < _r.IndexOf(c => c.Contains("mitmdump --mode")));
        Assert.Equal(d + "/out/flows.jsonl", NetnsEgressRouter.MitmFlowsPath(id));
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "deploy", "net", "inetsim"))) return d.FullName;
        throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public async Task Mitm_MissingCa_FailsClosed()
    {
        File.Delete(Path.Combine(_tmp, "ca", "mitmproxy-ca.pem"));
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None));
        Assert.Contains("CA", ex.Message);
        Assert.DoesNotContain(_r.Calls, c => c.Contains("mitmdump --mode"));
    }

    [Fact]
    public async Task Simulated_DirtyInetSimTemplate_FailsClosed_BeforeAnyCopy()
    {
        _r.TemplateOffender = "/var/lib/inetsim/http/evil-link\n";
        await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.Simulated, null, CancellationToken.None));
        Assert.DoesNotContain(_r.Calls, c => c.Contains(" cp "));
        Assert.DoesNotContain(_r.Calls, c => c.Contains("inetsim --config"));
        Assert.Empty(_r.Namespaces);
    }

    [Fact]
    public async Task Simulated_UsesPerRunInetSimData_AndReleaseRemovesIt()
    {
        var router = Router();
        var id = Guid.NewGuid();
        var n = RunNetNames.For(id);
        var data = $"/run/maldex/inetsim/{n.Namespace}/data";
        var lease = await router.CreateAsync(id, NetworkMode.Simulated, null, CancellationToken.None);
        Assert.Equal(data, NetnsEgressRouter.InetSimDataPath(id));
        var iDir = $"/run/maldex/inetsim/{n.Namespace}";
        var check = _r.IndexOf(c => c.StartsWith("sudo -n find /var/lib/inetsim ( -type l -o ! -user root -o -perm /022 )"));
        var root = _r.IndexOf(c => c == $"sudo -n install -d -m 0755 -o root -g root /run/maldex /run/maldex/inetsim {iDir} {data}");
        var cp = _r.IndexOf(c => c == "sudo -n cp -rP --no-preserve=ownership /var/lib/inetsim/. " + data + "/");
        Assert.True(check >= 0 && root > check && cp > root, "template check, then root-owned dirs, then copy");
        // INetSim runs in a user namespace: inside root = maldex-isroot (994:987), inside inetsim (110:112) = maldex-isuser
        // (993:986). Host root/inetsim own nothing it can write; real root only creates entries in root-owned parents.
        Assert.Contains(_r.Calls, c => c == $"sudo -n chown maldex-isroot:maldex-isuser {data}/certs/default_key.pem");
        Assert.Contains(_r.Calls, c => c == $"sudo -n chmod 0640 {data}/certs/default_key.pem");
        foreach (var w in new[] { "http/postdata", "ftp/upload" })
        {
            var rm = _r.IndexOf(c => c == $"sudo -n rm -rf -- {data}/{w}");
            var mk = _r.IndexOf(c => c == $"sudo -n install -d -m 0750 -o maldex-isuser -g maldex-isuser {data}/{w}");
            Assert.True(rm > cp && mk > rm, w);
        }
        var smtpRm = _r.IndexOf(c => c == $"sudo -n rm -rf -- {data}/smtp");
        var smtpMk = _r.IndexOf(c => c == $"sudo -n install -d -m 0755 -o root -g root {data}/smtp");
        Assert.True(smtpRm > cp && smtpMk > smtpRm, "smtp dir recreated root-owned");
        var start = _r.IndexOf(c => c.Contains("--config"));
        foreach (var mbox in new[] { "smtp.mbox", "smtps.mbox" })
        {
            var mf = _r.IndexOf(c => c == $"sudo -n install -m 0660 -o maldex-isroot -g maldex-isuser /dev/null {data}/smtp/{mbox}");
            Assert.True(mf > smtpMk && mf < start, mbox);
        }
        Assert.Contains(_r.Calls, c => c == $"sudo -n install -d -m 0755 -o root -g root {iDir}/log");
        foreach (var lf in new[] { "main.log", "service.log", "debug.log" })
            Assert.Contains(_r.Calls, c => c == $"sudo -n install -m 0660 -o maldex-isroot -g maldex-isuser /dev/null {iDir}/log/{lf}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n install -d -m 0755 -o root -g root {iDir}/report");
        Assert.Contains(_r.Calls, c => c == $"sudo -n install -d -m 0755 -o maldex-isroot -g maldex-isroot {iDir}/pid");
        // host 'inetsim' owns nothing; the only directories the mapped ids own are the two upload leaves and the pid dir
        Assert.DoesNotContain(_r.Calls, c => c.StartsWith("sudo", StringComparison.Ordinal) && (c.Contains("-o inetsim") || c.Contains("-g inetsim") || c.Contains("chgrp inetsim")));
        var owned = _r.Where(c => c.Contains("install -d") && (c.Contains("-o maldex-is") || c.Contains("-g maldex-is"))).Select(c => c.Split(' ')[^1]).OrderBy(x => x).ToList();
        Assert.Equal(new[] { $"{data}/ftp/upload", $"{data}/http/postdata", $"{iDir}/pid" }.OrderBy(x => x), owned);
        // low ports without any capability in the init user namespace (per-netns sysctl), set before INetSim starts
        var ports = _r.IndexOf(c => c == $"sudo -n ip netns exec {n.Namespace} sysctl -w net.ipv4.ip_unprivileged_port_start=0");
        Assert.True(ports >= 0 && ports < start);
        var inet = Assert.Single(_r.Where(c => c.Contains("--config")));
        Assert.Contains($"--data-dir {data} --log-dir {iDir}/log --report-dir {iDir}/report", inet);
        Assert.Contains($"--pidfile {iDir}/pid/inetsim.pid", inet);
        // real root sets up IPC + read-only mount namespaces, then the launcher enters the user namespace last
        Assert.Contains($"ip netns exec {n.Namespace} unshare --ipc --mount --pid --fork --mount-proc --propagation private -- python3 -I -c "
            + NetnsEgressRouter.UserNsLauncherScript + " 0 994 1\n110 993 1\n 0 987 1\n112 986 1\n " + iDir + " "
            + NetnsEgressRouter.InetSimSandboxScript + " inetsim --config", inet);
        Assert.Contains("CLONE_NEWUSER", NetnsEgressRouter.UserNsLauncherScript);
        var confCopy = _r.IndexOf(c => c.StartsWith("sudo -n install -m 0644 -o root -g root ") && c.EndsWith($"inetsim.conf {iDir}/inetsim.conf"));
        Assert.True(confCopy >= 0 && confCopy < start, "root-owned config copy staged before start");
        Assert.Contains($"inetsim --config {iDir}/inetsim.conf ", inet);
        Assert.Contains("remount,bind,ro", NetnsEgressRouter.InetSimSandboxScript);
        Assert.Contains("exit 1", NetnsEgressRouter.InetSimSandboxScript);
        Assert.Contains("mount -t proc", NetnsEgressRouter.InetSimSandboxScript); // the PID namespace's own /proc, mounted once // fails closed if anything stays writable
        Assert.Contains("create_reports          no", File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "net", "inetsim", "inetsim.conf")));
        Assert.DoesNotContain("--data-dir /var/lib/inetsim", inet);
        await router.ReleaseAsync(lease, CancellationToken.None);
        Assert.Contains(_r.Calls, c => c == $"sudo -n rm -rf -- /run/maldex/inetsim/{n.Namespace}");
    }

    [Theory]
    [InlineData("maldex-isroot")]
    [InlineData("maldex-isuser")]
    [InlineData("inetsim")]
    public async Task Simulated_MissingUserNamespaceIds_FailsClosed(string user)
    {
        _r.MissingUser = user;
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(id, NetworkMode.Simulated, null, CancellationToken.None));
        Assert.DoesNotContain(_r.Calls, c => c.Contains("--config"));
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip netns del {RunNetNames.For(id).Namespace}");
    }

    [Fact]
    public async Task CountryIgnoredForNonVpnModes()
    {
        var router = Router();
        await router.CreateAsync(Guid.NewGuid(), NetworkMode.None, "nl", CancellationToken.None);
        await router.CreateAsync(Guid.NewGuid(), NetworkMode.Simulated, "nl", CancellationToken.None);
        WriteWg("nl", GoodWg);
        await router.CreateAsync(Guid.NewGuid(), NetworkMode.LiveVpn, "nl", CancellationToken.None); // not blocked by the two above
    }

    [Fact]
    public async Task WgSetconfFailure_DoesNotLeakStderr()
    {
        WriteWg("nl", GoodWg);
        _r.Fail = c => c.Contains("wg setconf");
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.LiveVpn, "nl", CancellationToken.None));
        Assert.DoesNotContain("scripted failure", ex.Message);
        Assert.DoesNotContain("PrivateKey", ex.Message);
        Assert.Empty(_r.Namespaces);
    }

    [Fact]
    public async Task Mitm_IsTcpOnly_BoundToGateway_WithGuestTrustedCa()
    {
        var id = Guid.NewGuid();
        await Router().CreateAsync(id, NetworkMode.None, null, CancellationToken.None);
        var m = Assert.Single(_r.Where(c => c.Contains("mitmdump --mode transparent")));
        Assert.Contains("--listen-host 10.213.0.1", m);
        Assert.Contains("--set http3=false", m);
        Assert.Contains("--set confdir=/run/maldex/mitm/mxn", m);
        Assert.Contains("/conf ", m);
        Assert.DoesNotContain("udp", m, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--mode wireguard", m);
    }

    /// <summary>Malware infrastructure is often self-signed: the analysis proxy must not refuse the upstream (INetSim's own cert in Simulated).</summary>
    [Theory]
    [InlineData(NetworkMode.Simulated)]
    [InlineData(NetworkMode.LiveVpn)]
    public async Task Mitm_DoesNotVerifyUpstreamCertificates(NetworkMode mode)
    {
        if (mode == NetworkMode.LiveVpn) WriteWg("nl", GoodWg);
        await Router().CreateAsync(Guid.NewGuid(), mode, mode == NetworkMode.LiveVpn ? "nl" : null, CancellationToken.None);
        var m = Assert.Single(_r.Where(c => c.Contains("mitmdump --mode transparent")));
        Assert.Contains("--set ssl_insecure=true", m);
    }

    [Fact]
    public async Task EveryCommand_IsSudoN_WithDiscreteArgs()
    {
        WriteWg("nl", GoodWg);
        await Router().CreateAsync(Guid.NewGuid(), NetworkMode.Simulated, null, CancellationToken.None);
        await Router().CreateAsync(Guid.NewGuid(), NetworkMode.LiveVpn, "nl", CancellationToken.None);
        Assert.All(_r.Calls.Where(c => !c.StartsWith("id -", StringComparison.Ordinal)), c => Assert.StartsWith("sudo -n ", c)); // unprivileged id lookups
    }

    // ---- host-side invariants ----

    [Fact]
    public async Task HostBridge_NeverGetsAnAddress()
    {
        WriteWg("nl", GoodWg);
        var router = Router();
        await router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        await router.CreateAsync(Guid.NewGuid(), NetworkMode.Simulated, null, CancellationToken.None);
        await router.CreateAsync(Guid.NewGuid(), NetworkMode.LiveVpn, "nl", CancellationToken.None);
        // every `ip addr add` runs inside a namespace, never against the host ns, and never on an mxb/mxv/mxt link
        foreach (var c in _r.Where(c => c.Contains("ip addr ")))
        {
            Assert.Contains("ip netns exec", c);
            Assert.DoesNotContain("dev mxb", c);
            Assert.DoesNotContain("dev mxt", c);
            Assert.DoesNotContain("dev mxv", c);
        }
    }

    [Fact]
    public async Task Create_RefusesWhenBrNetfilterActive()
    {
        var flag = Path.Combine(_tmp, "bnf");
        File.WriteAllText(flag, "1\n");
        var router = new NetnsEgressRouter(Options.Create(new NetOptions { RunDir = Path.Combine(_tmp, "run") }), _r, null,
            new NetnsRouterTuning { BrNetfilterPath = flag });
        await Assert.ThrowsAsync<EgressUnavailableException>(() => router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None));
        Assert.DoesNotContain(_r.Calls, c => c.Contains("ip netns add"));
    }

    [Fact]
    public async Task Create_RefusesWhenHostGuardMissing()
    {
        _r.GuardPresent = false;
        await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None));
        Assert.DoesNotContain(_r.Calls, c => c.Contains("ip netns add"));
    }

    // ---- release / reuse / list ----

    [Fact]
    public async Task Release_Idempotent_KillsNamespacePids()
    {
        var router = Router();
        var lease = await router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        var n = RunNetNames.For(lease.TaskId);
        _r.Pids = "4242 4243\n";
        await router.ReleaseAsync(lease, CancellationToken.None);
        Assert.Contains(_r.Calls, c => c == "sudo -n kill -TERM 4242");
        Assert.Contains(_r.Calls, c => c == "sudo -n kill -TERM 4243");
        Assert.Contains(_r.Calls, c => c == "sudo -n kill -KILL 4242"); // survivors (the scripted pids never go away)
        var iKill = _r.IndexOf(c => c.Contains("kill -KILL"));
        Assert.True(iKill < _r.Calls.FindLastIndex(c => c == $"sudo -n ip link del {n.Bridge}"));
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip link del {n.Tap}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip link del {n.VethHost}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip netns del {n.Namespace}");

        _r.Pids = "";
        _r.Fail = c => c.Contains("ip link del") || c.Contains("ip netns del") || c.Contains("netns pids") || c.Contains("rm -rf");
        await router.ReleaseAsync(lease, CancellationToken.None); // second release tolerates "does not exist"
        await router.ReleaseAsync(new NetworkLease(Guid.NewGuid(), "mxt000000000000"), CancellationToken.None);
    }

    [Fact]
    public async Task Release_CancelledToken_StillTearsEverythingDown_AndFreesTheSlot()
    {
        var warnings = new List<string>();
        var router = Router(warnings.Add);
        var lease = await router.CreateAsync(Guid.NewGuid(), NetworkMode.Simulated, null, CancellationToken.None);
        var n = RunNetNames.For(lease.TaskId);
        _r.HonorCancellation = true;
        _r.Pids = "4242\n";
        _r.Calls.Clear();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await router.ReleaseAsync(lease, cts.Token); // teardown is not cancellable: it completes and returns
        Assert.Contains(_r.Calls, c => c == "sudo -n kill -TERM 4242");
        Assert.Contains(_r.Calls, c => c == "sudo -n kill -KILL 4242");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip link del {n.Bridge}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip netns del {n.Namespace}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n rm -rf -- /run/maldex/mitm/{n.Namespace}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n rm -rf -- /run/maldex/inetsim/{n.Namespace}");
        // the slot is free again: the same subnet index is handed out next
        _r.Pids = "";
        var next = await router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        var leaseJson = File.ReadAllText(Path.Combine(_tmp, "run", "net", RunNetNames.For(next.TaskId).Namespace, "lease.json"));
        Assert.Contains("\"Index\":0", leaseJson);
    }

    [Fact]
    public async Task Release_Again_WithCancelledToken_KeepsFlowsFromTheFirstRelease()
    {
        var router = Router();
        var lease = await router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        var kept = Path.Combine(_tmp, "run", "net", RunNetNames.For(lease.TaskId).Namespace, "flows.jsonl");
        await router.ReleaseAsync(lease, CancellationToken.None);
        File.WriteAllText(kept, "{\"kept\":1}\n"); // as if the first release had kept a capture
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await router.ReleaseAsync(lease, cts.Token);
        Assert.Equal("{\"kept\":1}\n", File.ReadAllText(kept));
        Assert.False(File.Exists(kept + ".partial"));
    }

    private NetnsEgressRouter RouterWithFlowsAt(string source) => new(Options.Create(new NetOptions
    {
        RunDir = Path.Combine(_tmp, "run"), WireGuardConfigDir = Path.Combine(_tmp, "wg"), MitmConfDir = Path.Combine(_tmp, "ca"),
        DeployNetDir = Path.Combine(_tmp, "deploy"), ControlPlaneIps = ["192.168.50.7"],
    }), _r, null, new NetnsRouterTuning
    {
        PollInterval = TimeSpan.FromMilliseconds(5), MitmWait = TimeSpan.FromMilliseconds(100), KillGrace = TimeSpan.FromMilliseconds(50),
        BrNetfilterPath = Path.Combine(_tmp, "no-br-netfilter"), FlowsSource = _ => source,
    });

    [Fact]
    public async Task Release_KeepsFlows_UnlessTheCaptureAlreadyStoredThem()
    {
        var source = Path.Combine(_tmp, "mitm-flows.jsonl");
        File.WriteAllText(source, "{\"url\":\"http://203.0.113.5/\"}\n");
        var router = RouterWithFlowsAt(source);

        var plain = await router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        await router.ReleaseAsync(plain, CancellationToken.None);
        Assert.Equal(File.ReadAllText(source), File.ReadAllText(Path.Combine(_tmp, "run", "net", RunNetNames.For(plain.TaskId).Namespace, "flows.jsonl")));

        var stored = await router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        Assert.True(NetnsEgressRouter.MarkFlowsStored(Path.Combine(_tmp, "run"), stored.TaskId));
        await router.ReleaseAsync(stored, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(_tmp, "run", "net", RunNetNames.For(stored.TaskId).Namespace, "flows.jsonl")), "no second copy of stored flows");
        Assert.False(NetnsEgressRouter.MarkFlowsStored(Path.Combine(_tmp, "run"), Guid.NewGuid())); // never creates a run dir
    }

    [Fact]
    public async Task RunNetDir_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        var lease = await Router().CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        var dir = Path.Combine(_tmp, "run", "net", RunNetNames.For(lease.TaskId).Namespace);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
    }

    [Fact]
    public async Task Release_NoFlowsFile_IsNotAWarning()
    {
        var warnings = new List<string>();
        var router = Router(warnings.Add);
        var lease = await router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        await router.ReleaseAsync(lease, CancellationToken.None); // capture never ran: nothing to keep
        Assert.DoesNotContain(warnings, w => w.Contains("flows", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SubnetIndex_ReusedAfterRelease()
    {
        var router = Router();
        var a = await router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        var b = await router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        Assert.Contains(_r.Calls, c => c.Contains("ip addr add 10.213.0.1/30"));
        Assert.Contains(_r.Calls, c => c.Contains("ip addr add 10.213.0.5/30"));
        await router.ReleaseAsync(a, CancellationToken.None);
        _r.Calls.Clear();
        await router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        Assert.Contains(_r.Calls, c => c.Contains("ip addr add 10.213.0.1/30"));
        Assert.DoesNotContain(_r.Calls, c => c.Contains("ip addr add 10.213.0.5/30"));
        _ = b;
    }

    [Fact]
    public async Task Create_FailsWhenAllSlotsTaken()
    {
        var router = new NetnsEgressRouter(Options.Create(new NetOptions { RunDir = Path.Combine(_tmp, "run"), MaxRuns = 1, MitmConfDir = Path.Combine(_tmp, "ca"), DeployNetDir = Path.Combine(_tmp, "deploy") }), _r, null,
            new NetnsRouterTuning { PollInterval = TimeSpan.FromMilliseconds(5), MitmWait = TimeSpan.FromMilliseconds(100), BrNetfilterPath = Path.Combine(_tmp, "x") });
        await router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        await Assert.ThrowsAsync<EgressUnavailableException>(() => router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None));
    }

    [Fact]
    public async Task List_MapsLeases_SkipsUnknown_AndDeletesOrphanLinks()
    {
        var router = Router();
        var lease = await router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        _r.Namespaces.Add("mxnaaaaaaaaaaaa"); // no lease.json
        var warnings = new List<string>();
        var listing = await Router(warnings.Add).ListAsync(CancellationToken.None);
        Assert.Equal([lease], listing);
        Assert.Single(warnings);
    }

    [Fact]
    public async Task ReleaseOrphans_SkipsActiveRuns_AndCoversWgLinks()
    {
        var id = Guid.Parse("00112233445566778899aabbccddeeff");
        var h = id.ToString("N")[..12];
        var router = Router(null, new LinkListingRunner(_r, $"5: mxt{h}: <NO-CARRIER> mtu 1500\n6: mxwdeadbeef0002: <NO-CARRIER> mtu 1500\n"));
        await router.CreateAsync(id, NetworkMode.None, null, CancellationToken.None);
        _r.Namespaces.Clear(); // namespace not listed, but the run is active in this process
        _r.Calls.Clear();
        await router.ReleaseOrphansAsync(CancellationToken.None);
        Assert.DoesNotContain(_r.Calls, c => c == $"sudo -n ip link del mxt{h}");
        Assert.Contains(_r.Calls, c => c == "sudo -n ip link del mxwdeadbeef0002");
    }

    [Fact]
    public async Task ReleaseOrphans_DeletesOnlyMxLinksWithoutNamespace()
    {
        var router = Router();
        var live = await router.CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        var liveH = live.NetworkId[3..];
        var runner = new LinkListingRunner(_r, $"""
            1: lo: <LOOPBACK> mtu 65536
            2: eth0: <BROADCAST> mtu 1500
            8: mxwdeadbeef0001: <NO-CARRIER> mtu 1500
            9: mxbdeadbeef0001: <NO-CARRIER> mtu 1500
            10: mxvdeadbeef0001@if3: <NO-CARRIER> mtu 1500
            11: mxtdeadbeef0001: <NO-CARRIER> mtu 1500
            12: mxt{liveH}: <NO-CARRIER> mtu 1500
            13: mxother: <NO-CARRIER> mtu 1500
            """);
        var r2 = new NetnsEgressRouter(Options.Create(new NetOptions { RunDir = Path.Combine(_tmp, "run") }), runner, null, new NetnsRouterTuning());
        await r2.ReleaseOrphansAsync(CancellationToken.None);
        var dels = _r.Calls.Where(c => c.StartsWith("sudo -n ip link del ")).ToList();
        Assert.Equal(["sudo -n ip link del mxwdeadbeef0001", "sudo -n ip link del mxbdeadbeef0001", "sudo -n ip link del mxvdeadbeef0001", "sudo -n ip link del mxtdeadbeef0001"], dels);
    }

    private sealed class LinkListingRunner(ScriptedRunner inner, string linkOutput) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null, TimeSpan? timeout = null)
        {
            if (string.Join(' ', args) == "-n ip -o link show") return Task.FromResult(new ProcessResult(0, linkOutput, ""));
            return inner.RunAsync(file, args, ct, stdin, timeout);
        }
    }

    // ---- Direct egress (template edit sessions) ----

    private async Task<(NetnsEgressRouter Router, NetworkLease Lease, RunNetNames N)> CreateDirect(string[]? dns = null)
    {
        var router = Router(dns: dns);
        var lease = await router.CreateAsync(Guid.NewGuid(), NetworkMode.Direct, null, CancellationToken.None);
        return (router, lease, RunNetNames.For(lease.TaskId));
    }

    private string HostTableScript(RunNetNames n) =>
        _r.Stdins.Single(s => s is not null && s.Contains($"table inet {n.DirectTable} {{") && s.Contains("chain postrouting"))!;

    [Fact]
    public async Task Direct_BuildsUplink_HostTableFirst_AndNoInterception()
    {
        var (_, lease, n) = await CreateDirect();
        Assert.Equal(n.Tap, lease.NetworkId);

        // the host table (with the default-route interface) and the namespace ruleset are loaded before any link exists
        var hostTable = _r.Stdins.FindIndex(s => s is not null && s.Contains($"table inet {n.DirectTable} {{"));
        Assert.True(hostTable >= 0, "host table not loaded");
        Assert.Equal("sudo -n nft -f -", _r.Calls[hostTable]);
        Assert.Contains($"iifname \"{n.UplinkHost}\" oifname \"eth0\" masquerade", _r.Stdins[hostTable]!);
        Assert.True(hostTable < _r.IndexOf(c => c.Contains("ip link add")));
        Assert.True(hostTable < _r.IndexOf(c => c.Contains($"link set {n.UplinkHost} up")));
        var nsRules = _r.Calls.FindIndex(c => c == $"sudo -n ip netns exec {n.Namespace} nft -f -");
        Assert.True(nsRules >= 0 && nsRules < _r.IndexOf(c => c.Contains("ip link add")));
        Assert.Contains("masquerade", _r.Stdins[nsRules]!);
        Assert.DoesNotContain("redirect", _r.Stdins[nsRules]!);

        // the uplink pair, addressed from the pool by slot (0 here), default route out of it
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip link add {n.UplinkHost} type veth peer name {n.UplinkNs} netns {n.Namespace}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n sysctl -w net.ipv6.conf.{n.UplinkHost}.disable_ipv6=1");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip addr add 100.127.0.1/30 dev {n.UplinkHost}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip link set {n.UplinkHost} up");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip netns exec {n.Namespace} ip addr add 100.127.0.2/30 dev {n.UplinkNs}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip netns exec {n.Namespace} ip link set {n.UplinkNs} up");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip netns exec {n.Namespace} sysctl -w net.ipv4.ip_forward=1");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip netns exec {n.Namespace} ip route add default via 100.127.0.1 dev {n.UplinkNs}");
        // the host bridge stays address-less
        Assert.DoesNotContain(_r.Calls, c => c.Contains("ip addr add") && c.Contains(n.Bridge));

        // no interception, capture or simulated internet
        Assert.DoesNotContain(_r.Calls, c => c.Contains("mitmdump") || c.Contains("inetsim") || c.Contains("tcpdump") || c.Contains("route add local"));
        Assert.DoesNotContain(_r.Calls, c => c.Contains("maldex-mitm"));
        Assert.Contains("\"Mode\":\"Direct\"", File.ReadAllText(Path.Combine(_tmp, "run", "net", n.Namespace, "lease.json")));
    }

    [Fact]
    public async Task Direct_DnsmasqStaysUnprivileged_AndForwardsToTheHostsUpstreamResolvers()
    {
        var (_, _, n) = await CreateDirect();
        var dnsmasq = Assert.Single(_r.Calls, c => c.Contains(" dnsmasq "));
        Assert.Contains("setpriv --reuid=maldex-dns --regid=maldex-dns --clear-groups --no-new-privs --inh-caps=-all --bounding-set=-all -- dnsmasq", dnsmasq);
        Assert.Contains("--no-resolv", dnsmasq);
        Assert.Contains("--server=192.168.3.10 --server=8.8.8.8", dnsmasq); // the IPv6 and 'search' lines of resolv.conf are ignored
        Assert.DoesNotContain("2001:", dnsmasq);
        Assert.DoesNotContain("--address=", dnsmasq);
        Assert.Contains($"--interface={n.VethNs}", dnsmasq);
    }

    [Fact]
    public async Task Direct_ConfiguredDnsServers_ReplaceTheHostResolvers()
    {
        var (_, _, n) = await CreateDirect(["9.9.9.9"]);
        var dnsmasq = Assert.Single(_r.Calls, c => c.Contains(" dnsmasq "));
        Assert.Contains("--server=9.9.9.9", dnsmasq);
        Assert.DoesNotContain("8.8.8.8", dnsmasq);
        var ns = _r.Stdins[_r.Calls.FindIndex(c => c == $"sudo -n ip netns exec {n.Namespace} nft -f -")]!;
        Assert.Contains("ip daddr { 9.9.9.9 } udp dport 53 accept", ns);
    }

    [Fact]
    public async Task Direct_ReadsTheDefaultRouteInterface_PerLease()
    {
        _r.DefaultRoute = "default via 10.0.0.1 dev enp3s0 proto static metric 20\ndefault via 192.168.1.1 dev wlan0 metric 600\n";
        var (_, _, n) = await CreateDirect();
        var table = HostTableScript(n);
        Assert.Contains("oifname \"enp3s0\" masquerade", table);
        Assert.DoesNotContain("wlan0", table);
        // the next lease re-reads it (the host may have changed networks)
        _r.DefaultRoute = "default via 192.168.1.1 dev wlan0 metric 600\n";
        var second = await Router().CreateAsync(Guid.NewGuid(), NetworkMode.Direct, null, CancellationToken.None);
        Assert.Contains("oifname \"wlan0\" masquerade", HostTableScript(RunNetNames.For(second.TaskId)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("192.168.1.0/24 dev eth0 scope link\n")]
    [InlineData("default via 10.9.9.9 dev mxu0123456789ab\n")]
    [InlineData("default via 10.9.9.9 dev lo\n")]
    [InlineData("default via 10.9.9.9 dev e\"th0\n")]
    public async Task Direct_NoUsableDefaultRoute_IsEgressUnavailable_AndNothingIsTouched(string route)
    {
        _r.DefaultRoute = route;
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.Direct, null, CancellationToken.None));
        Assert.Contains("default route", ex.Message);
        Assert.DoesNotContain(_r.Calls, c => c.Contains("netns add") || c.Contains("link add") || c.Contains("nft -f"));
        Assert.Empty(_r.Namespaces);
    }

    [Fact]
    public async Task Direct_NoResolvers_IsEgressUnavailable()
    {
        File.WriteAllText(ResolvConf, "# nothing\nnameserver ::1\nnameserver 127.0.0.53\n");
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.Direct, null, CancellationToken.None));
        Assert.Contains("DNS", ex.Message);
        Assert.DoesNotContain(_r.Calls, c => c.Contains("netns add"));
        File.Delete(ResolvConf);
        await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.Direct, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("192.168.50.7")] // a control-plane address
    [InlineData("127.0.0.1")]
    [InlineData("not-an-ip")]
    public async Task Direct_UnsafeConfiguredResolver_IsEgressUnavailable(string dns)
    {
        await Assert.ThrowsAsync<EgressUnavailableException>(() => Router(dns: [dns]).CreateAsync(Guid.NewGuid(), NetworkMode.Direct, null, CancellationToken.None));
        Assert.DoesNotContain(_r.Calls, c => c.Contains("netns add"));
    }

    [Theory]
    [InlineData("0\n")]
    [InlineData("garbage")]
    public async Task Direct_HostNotForwarding_IsEgressUnavailable(string value)
    {
        File.WriteAllText(IpForward, value);
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.Direct, null, CancellationToken.None));
        Assert.Contains("forward", ex.Message);
        Assert.DoesNotContain(_r.Calls, c => c.Contains("netns add"));
    }

    [Fact]
    public async Task Direct_BadPool_IsEgressUnavailable()
    {
        var router = new NetnsEgressRouter(Options.Create(new NetOptions { RunDir = Path.Combine(_tmp, "run"), DirectUplinkPool = "nonsense", ControlPlaneIps = ["192.168.50.7"] }), _r, null,
            new NetnsRouterTuning { BrNetfilterPath = Path.Combine(_tmp, "none"), ResolvConfPath = ResolvConf, IpForwardPath = IpForward });
        await Assert.ThrowsAsync<EgressUnavailableException>(() => router.CreateAsync(Guid.NewGuid(), NetworkMode.Direct, null, CancellationToken.None));
        Assert.DoesNotContain(_r.Calls, c => c.Contains("netns add"));
    }

    [Fact]
    public async Task Direct_EachLeaseGetsItsOwnUplinkSubnetAndTable()
    {
        var router = Router();
        var a = await router.CreateAsync(Guid.NewGuid(), NetworkMode.Direct, null, CancellationToken.None);
        var b = await router.CreateAsync(Guid.NewGuid(), NetworkMode.Direct, null, CancellationToken.None);
        var nb = RunNetNames.For(b.TaskId);
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip addr add 100.127.0.5/30 dev {nb.UplinkHost}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip netns exec {nb.Namespace} ip route add default via 100.127.0.5 dev {nb.UplinkNs}");
        Assert.NotEqual(RunNetNames.For(a.TaskId).DirectTable, nb.DirectTable);
        await router.ReleaseAsync(a, CancellationToken.None);
        Assert.DoesNotContain(_r.Calls, c => c == $"sudo -n nft delete table inet {nb.DirectTable}"); // releasing one never touches the other's table
    }

    [Theory]
    [InlineData("ip route add default via")]
    [InlineData("dnsmasq")]
    [InlineData("ip link add mxu")]
    public async Task Direct_SetupFailure_TearsDownUplinkAndTable(string failing)
    {
        var id = Guid.NewGuid();
        var n = RunNetNames.For(id);
        _r.Fail = c => c.Contains(failing) && !c.Contains("ip link del") && !c.Contains("netns del");
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(id, NetworkMode.Direct, null, CancellationToken.None));
        Assert.Contains("scripted failure", ex.Message);
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip link del {n.UplinkHost}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n nft delete table inet {n.DirectTable}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip netns del {n.Namespace}");
        Assert.Empty(_r.Namespaces);
    }

    [Fact]
    public async Task Direct_Release_RemovesUplinkAndHostTable_Idempotently()
    {
        var (router, lease, n) = await CreateDirect();
        _r.Calls.Clear();
        await router.ReleaseAsync(lease, CancellationToken.None);
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip link del {n.UplinkHost}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n nft delete table inet {n.DirectTable}");
        _r.Fail = c => c.Contains("ip link del") || c.Contains("nft delete") || c.Contains("netns");
        await router.ReleaseAsync(lease, CancellationToken.None); // "does not exist" is tolerated
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Direct_WithoutTheDockerUserException_IsEgressUnavailable_AndNothingIsTouched(bool outRule, bool backRule)
    {
        _r.DockerRules = [outRule, backRule];
        var ex = await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.Direct, null, CancellationToken.None));
        Assert.Equal("Direct egress needs the Maldex exception in Docker's DOCKER-USER chain; run ./build.sh real-setup", ex.Message);
        Assert.DoesNotContain(_r.Calls, c => c.Contains("netns add") || c.Contains("link add") || c.Contains("nft -f"));
    }

    [Fact]
    public async Task Direct_DockerUserException_IsCheckedForTheCurrentDefaultInterface_ReadOnly()
    {
        _r.DockerRulesIface = "eth0";
        await CreateDirect();
        var checks = _r.Calls.Where(c => c.Contains("iptables")).ToList();
        Assert.Equal(
        [
            "sudo -n iptables -C DOCKER-USER -i mxu+ -o eth0 -m comment --comment maldex-direct -j ACCEPT",
            "sudo -n iptables -C DOCKER-USER -i eth0 -o mxu+ -m conntrack --ctstate RELATED,ESTABLISHED -m comment --comment maldex-direct -j ACCEPT",
        ], checks); // only -C: the router never edits Docker's chains

        // the host moved to another interface and the exception was not re-installed for it: fail closed
        _r.DefaultRoute = "default via 192.168.1.1 dev wlan0 metric 600\n";
        await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.Direct, null, CancellationToken.None));
    }

    [Fact]
    public async Task OtherModes_NeverCheckDocker()
    {
        await Router().CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None);
        Assert.DoesNotContain(_r.Calls, c => c.Contains("iptables"));
    }

    [Fact]
    public async Task Direct_HostChecks_StillApply()
    {
        _r.GuardPresent = false;
        await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.Direct, null, CancellationToken.None));
        Assert.DoesNotContain(_r.Calls, c => c.Contains("netns add"));
    }

    [Fact]
    public async Task OtherModes_NeverTouchTheUplinkOrHostTable_OnCreate()
    {
        var router = Router();
        await router.CreateAsync(Guid.NewGuid(), NetworkMode.Simulated, null, CancellationToken.None);
        Assert.DoesNotContain(_r.Calls, c => c.Contains("mxu") || c.Contains("maldex_direct") || c == "ip -4 -o route show default");
        Assert.DoesNotContain(_r.Stdins, s => s is not null && s.Contains("maldex_direct"));
    }

    // ---- IsolateAsync (controller ruling R2): cut a running lease off from all egress ----

    [Fact]
    public async Task Isolate_Direct_SwitchesToNoEgress_RemovesRouteAndHostTable_KeepsTheVmsLinks()
    {
        var (router, lease, n) = await CreateDirect();
        _r.Calls.Clear();
        _r.Stdins.Clear();
        await router.IsolateAsync(lease, CancellationToken.None);

        var apply = _r.Calls.FindIndex(c => c == $"sudo -n ip netns exec {n.Namespace} nft -f -");
        Assert.True(apply >= 0, "no ruleset applied");
        var rules = _r.Stdins[apply]!;
        Assert.DoesNotContain("masquerade", rules);
        Assert.DoesNotContain(n.UplinkNs, rules);
        Assert.Contains("table inet maldex {", rules);
        Assert.Contains("policy drop;", rules);
        var delRoute = _r.Calls.FindIndex(c => c == $"sudo -n ip netns exec {n.Namespace} ip route del default");
        var delTable = _r.Calls.FindIndex(c => c == $"sudo -n nft delete table inet {n.DirectTable}");
        var delLink = _r.Calls.FindIndex(c => c == $"sudo -n ip link del {n.UplinkHost}");
        Assert.True(apply < delRoute && delTable >= 0 && delLink >= 0, string.Join('\n', _r.Calls));
        Assert.True(delRoute < delLink && delLink < delTable, "teardown order must match release (uplink link, then host table):\n" + string.Join('\n', _r.Calls));
        // the VM's own attachment (tap, bridge, guest veth), dnsmasq and the namespace are untouched
        Assert.DoesNotContain(_r.Calls, c => c.Contains($"link del {n.Tap}") || c.Contains($"link del {n.Bridge}") || c.Contains($"link del {n.VethHost}") || c.Contains("netns del") || c.Contains("kill"));
    }

    [Fact]
    public async Task Isolate_IsIdempotent_AndReleaseStillTearsEverythingDown()
    {
        var (router, lease, n) = await CreateDirect();
        await router.IsolateAsync(lease, CancellationToken.None);
        _r.Fail = c => c.Contains("route del") || c.Contains("nft delete") || c.Contains("ip link del"); // already gone
        await router.IsolateAsync(lease, CancellationToken.None);
        _r.Fail = _ => false;
        _r.Calls.Clear();
        await router.ReleaseAsync(lease, CancellationToken.None);
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip netns del {n.Namespace}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip link del {n.Bridge}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n nft delete table inet {n.DirectTable}");
    }

    [Fact]
    public async Task Isolate_RulesetFailure_StillCutsTheHostSide_ThenThrows()
    {
        var (router, lease, n) = await CreateDirect();
        _r.Calls.Clear();
        _r.Fail = c => c == $"sudo -n ip netns exec {n.Namespace} nft -f -";
        await Assert.ThrowsAsync<EgressUnavailableException>(() => router.IsolateAsync(lease, CancellationToken.None));
        Assert.Contains(_r.Calls, c => c == $"sudo -n nft delete table inet {n.DirectTable}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip link del {n.UplinkHost}");
        Assert.Contains(_r.Calls, c => c == $"sudo -n ip netns exec {n.Namespace} ip route del default");
    }

    [Fact]
    public async Task Isolate_NonDirectLease_OnlyReplacesTheRuleset()
    {
        var router = Router();
        var lease = await router.CreateAsync(Guid.NewGuid(), NetworkMode.Simulated, null, CancellationToken.None);
        var n = RunNetNames.For(lease.TaskId);
        _r.Calls.Clear();
        _r.Stdins.Clear();
        await router.IsolateAsync(lease, CancellationToken.None);
        var apply = _r.Calls.FindIndex(c => c == $"sudo -n ip netns exec {n.Namespace} nft -f -");
        Assert.True(apply >= 0);
        Assert.DoesNotContain("ct status & dnat == 0 meta l4proto", _r.Stdins[apply]!); // Simulated's any-IP accept is gone
        Assert.DoesNotContain(_r.Calls, c => c.Contains("route del") || c.Contains("nft delete") || c.Contains("ip link del"));
    }

    [Fact]
    public async Task Isolate_UnknownOrReleasedLease_IsANoOp_ButCleansHostRemnants()
    {
        var router = Router();
        var ghost = new NetworkLease(Guid.NewGuid(), "mxt000000000000");
        await router.IsolateAsync(ghost, CancellationToken.None);
        Assert.DoesNotContain(_r.Calls, c => c.Contains("nft -f -"));
        Assert.Contains(_r.Calls, c => c == $"sudo -n nft delete table inet {RunNetNames.For(ghost.TaskId).DirectTable}");
    }

    [Fact]
    public async Task Isolate_LiveNamespaceWithUnreadableLease_FailsClosedLoudly()
    {
        var (router, lease, n) = await CreateDirect();
        File.Delete(Path.Combine(_tmp, "run", "net", n.Namespace, "lease.json"));
        await Assert.ThrowsAsync<EgressUnavailableException>(() => router.IsolateAsync(lease, CancellationToken.None));
        Assert.Contains(_r.Calls, c => c == $"sudo -n nft delete table inet {n.DirectTable}"); // the host side is cut regardless
    }

    // ---- orphans ----

    [Fact]
    public async Task ReleaseOrphans_RemovesStrayDirectTablesAndUplinks_ButNotLiveOnes()
    {
        var (_, _, n) = await CreateDirect();
        _r.NftTables = $"table ip nat\ntable inet maldex_guard\ntable inet {n.DirectTable}\ntable inet maldex_direct_deadbeef0001\ntable inet maldex_direct_notahex\n";
        var runner = new LinkListingRunner(_r, $"""
            2: eth0: <BROADCAST> mtu 1500
            3: mxudeadbeef0001@if4: <NO-CARRIER> mtu 1500
            4: {n.UplinkHost}@if5: <UP> mtu 1500
            """);
        var r2 = new NetnsEgressRouter(Options.Create(new NetOptions { RunDir = Path.Combine(_tmp, "run") }), runner, null, new NetnsRouterTuning());
        _r.Calls.Clear();
        await r2.ReleaseOrphansAsync(CancellationToken.None);
        Assert.Equal(["sudo -n nft delete table inet maldex_direct_deadbeef0001"], _r.Calls.Where(c => c.Contains("nft delete")).ToList());
        Assert.Equal(["sudo -n ip link del mxudeadbeef0001"], _r.Calls.Where(c => c.StartsWith("sudo -n ip link del ")).ToList());
    }

    [Fact]
    public void WireGuardConf_RequiresDns_AndKeepsOnlyWgKeys()
    {
        Assert.Throws<EgressUnavailableException>(() => WireGuardConf.Parse("[Interface]\nAddress = 10.0.0.2/32\nPrivateKey = x\n"));
        var c = WireGuardConf.Parse("[Interface]\r\nAddress = fd00::2/128, 10.0.0.2/32\r\nDNS = fd00::1, 10.0.0.1\r\nPrivateKey = k\r\nTable = off\r\nSaveConfig = true\r\n[Peer]\r\nPublicKey = p\r\nAllowedIPs = 0.0.0.0/0\r\n");
        Assert.Equal("10.0.0.2/32", c.Address);
        Assert.Equal("10.0.0.1", c.Dns);
        Assert.Null(c.EgressIp);
        Assert.DoesNotContain("Table", c.SetConfText);
        Assert.DoesNotContain("SaveConfig", c.SetConfText);
        Assert.Contains("PersistentKeepalive", c.SetConfText);
    }
}

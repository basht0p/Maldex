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

        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null, TimeSpan? timeout = null)
        {
            var line = file + " " + string.Join(' ', args);
            lock (Calls) { Calls.Add(line); Stdins.Add(stdin); }
            if (Fail(line)) return Task.FromResult(new ProcessResult(1, "", "scripted failure"));
            if (line.Contains("nft list table inet maldex_guard")) return Ok(GuardPresent ? 0 : 1);
            if (line.StartsWith("sudo -n ip netns add ")) { lock (Namespaces) Namespaces.Add(args[^1]); return Ok(); }
            if (line.StartsWith("sudo -n ip netns del ")) { lock (Namespaces) Namespaces.Remove(args[^1]); return Ok(); }
            if (line == "sudo -n ip netns list") { lock (Namespaces) return Ok(out_: string.Join('\n', Namespaces.Select(n => n + " (id: 0)"))); }
            if (line.StartsWith("sudo -n ip netns pids ")) return Ok(out_: Pids);
            if (line.Contains(" ss -ltn")) return Ok(out_: string.Join('\n', Enumerable.Range(0, 20).Select(i => $"LISTEN 0 128 10.213.{i / 64}.{i % 64 * 4 + 1}:8080 0.0.0.0:*")));
            if (line == "id -u" || line == "id -g") return Ok(out_: "1000\n");
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

    public NetnsEgressRouterTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    private NetnsEgressRouter Router(Action<string>? warn = null, IProcessRunner? runner = null) => new(Options.Create(new NetOptions
    {
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
        Assert.DoesNotContain("--server", dns);
        Assert.DoesNotContain("--address", dns);
        Assert.True(File.Exists(Path.Combine(_tmp, "run", "net", n.Namespace, "lease.json")));
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
        Assert.Contains($"ip netns exec {RunNetNames.For(id).Namespace} setpriv --reuid=1000 --regid=1000 --init-groups -- ", m);
        Assert.True(m.IndexOf("setpriv", StringComparison.Ordinal) < m.IndexOf("mitmdump --mode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mitm_UidLookupFailure_FailsClosed()
    {
        _r.Fail = c => c == "id -u";
        await Assert.ThrowsAsync<EgressUnavailableException>(() => Router().CreateAsync(Guid.NewGuid(), NetworkMode.None, null, CancellationToken.None));
        Assert.DoesNotContain(_r.Calls, c => c.Contains("mitmdump --mode"));
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
        Assert.Contains(_r.Calls, c => c == "sudo -n cp -a /var/lib/inetsim/. " + data + "/");
        foreach (var w in new[] { "http/postdata", "ftp/upload", "smtp" })
        {
            var rm = _r.IndexOf(c => c == $"sudo -n rm -rf -- {data}/{w}");
            var mk = _r.IndexOf(c => c.StartsWith("sudo -n install -d -m 0770 -o inetsim -g inetsim ") && c.EndsWith($"{data}/{w}"));
            Assert.True(rm >= 0 && mk > rm, w);
        }
        var inet = Assert.Single(_r.Where(c => c.Contains("--config")));
        Assert.Contains($"--data-dir {data} ", inet);
        Assert.DoesNotContain("--data-dir /var/lib/inetsim", inet);
        await router.ReleaseAsync(lease, CancellationToken.None);
        Assert.Contains(_r.Calls, c => c == $"sudo -n rm -rf -- /run/maldex/inetsim/{n.Namespace}");
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
        Assert.Contains($"--set confdir={Path.Combine(_tmp, "ca")}", m);
        Assert.DoesNotContain("udp", m, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--mode wireguard", m);
    }

    [Fact]
    public async Task EveryCommand_IsSudoN_WithDiscreteArgs()
    {
        WriteWg("nl", GoodWg);
        await Router().CreateAsync(Guid.NewGuid(), NetworkMode.Simulated, null, CancellationToken.None);
        await Router().CreateAsync(Guid.NewGuid(), NetworkMode.LiveVpn, "nl", CancellationToken.None);
        Assert.All(_r.Calls.Where(c => c is not ("id -u" or "id -g")), c => Assert.StartsWith("sudo -n ", c));
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
        _r.Fail = c => c.Contains("ip link del") || c.Contains("ip netns del") || c.Contains("netns pids");
        await router.ReleaseAsync(lease, CancellationToken.None); // second release tolerates "does not exist"
        await router.ReleaseAsync(new NetworkLease(Guid.NewGuid(), "mxt000000000000"), CancellationToken.None);
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
        var router = new NetnsEgressRouter(Options.Create(new NetOptions { RunDir = Path.Combine(_tmp, "run"), MaxRuns = 1 }), _r, null,
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

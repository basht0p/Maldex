using System.Diagnostics;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Net;
using Xunit;

namespace Maldex.Core.Tests.Net;

public class NftRulesetBuilderTests
{
    private static readonly Guid Id = Guid.Parse("0123456789abcdef0123456789abcdef");
    private static readonly string[] Ranges =
        ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10", "127.0.0.0/8", "224.0.0.0/4"];
    private static readonly string[] Cp = ["192.168.50.7", "203.0.113.9"];
    private const string Ns = "mxg0123456789ab";
    private const string Wg = "mxw0123456789ab";

    private static EgressTopology Topo(NetworkMode mode, string[]? cp = null, int index = 3, int mitm = 8080) =>
        new(RunNetNames.For(Id), RunSubnet.For(index), mode, cp ?? Cp, "198.18.0.10", mitm);

    private static readonly NetworkMode[] Modes = [NetworkMode.None, NetworkMode.Simulated, NetworkMode.LiveVpn];

    public static TheoryData<NetworkMode> AllModes => new(Modes);

    /// <summary>Returns the lines of one chain (between its opening line and closing brace); [0] is the type/policy line.</summary>
    private static List<string> Chain(string ruleset, string name)
    {
        var lines = ruleset.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        var start = lines.FindIndex(l => l == $"chain {name} {{");
        Assert.True(start >= 0, $"chain {name} missing");
        var end = lines.FindIndex(start, l => l == "}");
        return lines.Skip(start + 1).Take(end - start - 1).Where(l => l.Length > 0).ToList();
    }

    [Theory, MemberData(nameof(AllModes))]
    public void Ruleset_DropsHostControlPlaneAndRfc1918(NetworkMode mode)
    {
        var r = NftRulesetBuilder.Build(Topo(mode));
        foreach (var c in new[] { "input", "forward", "output" })
            Assert.Contains("policy drop;", Chain(r, c)[0]);

        var fwd = Chain(r, "forward");
        var firstAccept = fwd.FindIndex(l => l.EndsWith(" accept"));
        var lastDrop = fwd.FindLastIndex(l => l.EndsWith(" drop") && l.Contains("daddr"));
        Assert.True(lastDrop >= 0);
        if (firstAccept >= 0) Assert.True(lastDrop < firstAccept, "all drops must precede the accepts");
        var drops = string.Join('\n', fwd.Where(l => l.EndsWith(" drop") && l.Contains("daddr")));
        foreach (var range in Ranges) Assert.Contains(range, drops);
        foreach (var ip in Cp) Assert.Contains(ip, drops);
    }

    [Theory, MemberData(nameof(AllModes))]
    public void Ruleset_EmptyControlPlane_StillDropsPrivateRanges(NetworkMode mode)
    {
        var r = NftRulesetBuilder.Build(Topo(mode, []));
        var drops = string.Join('\n', Chain(r, "forward").Where(l => l.EndsWith(" drop")));
        foreach (var range in Ranges) Assert.Contains(range, drops);
        Assert.DoesNotContain("{ }", r);
        Assert.DoesNotContain("{  }", r);
    }

    [Theory, MemberData(nameof(AllModes))]
    public void Ruleset_Prelude_IsIdempotent(NetworkMode mode)
    {
        var lines = NftRulesetBuilder.Build(Topo(mode)).Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        var i = lines.IndexOf("table inet maldex {}");
        Assert.True(i >= 0);
        Assert.Equal("delete table inet maldex", lines[i + 1]);
        Assert.Equal("table inet maldex {", lines[i + 2]);
    }

    [Fact]
    public void Ipv6_DroppedEverywhere()
    {
        foreach (var mode in Modes)
        {
            var r = NftRulesetBuilder.Build(Topo(mode));
            foreach (var c in new[] { "input", "forward", "output" })
                Assert.Equal("meta nfproto ipv6 drop", Chain(r, c)[1]); // first rule, before every accept
            Assert.DoesNotContain("ip6 ", r);
        }
    }

    [Fact]
    public void None_HasNoForwardAccept_AndNoInetSimInput()
    {
        var r = NftRulesetBuilder.Build(Topo(NetworkMode.None));
        Assert.DoesNotContain(Chain(r, "forward"), l => l.EndsWith(" accept"));
        Assert.DoesNotContain(Chain(r, "input"), l => l.Contains("!="));
        Assert.DoesNotContain("masquerade", r);
        Assert.DoesNotContain(Wg, r);
    }

    [Fact]
    public void Simulated_HasNoForwardAccept_InputExcludesPrivateRanges()
    {
        var r = NftRulesetBuilder.Build(Topo(NetworkMode.Simulated));
        Assert.DoesNotContain(Chain(r, "forward"), l => l.EndsWith(" accept"));
        var sim = Chain(r, "input").Where(l => l.Contains("ip daddr !=")).ToList();
        Assert.NotEmpty(sim);
        foreach (var l in sim)
        {
            foreach (var range in Ranges) Assert.Contains(range, l);
            foreach (var ip in Cp) Assert.Contains(ip, l);
            Assert.EndsWith(" accept", l);
        }
        Assert.DoesNotContain("masquerade", r);
        Assert.DoesNotContain(Wg, r);
    }

    [Theory, MemberData(nameof(AllModes))]
    public void Input_NeverAcceptsToControlPlaneOrPrivate_ExceptGateway(NetworkMode mode)
    {
        var t = Topo(mode);
        foreach (var l in Chain(NftRulesetBuilder.Build(t), "input").Where(x => x.EndsWith(" accept")))
        {
            foreach (var ip in Cp) if (l.Contains(ip)) Assert.Contains("ip daddr !=", l);
            if (l.Contains("ip daddr ") && !l.Contains("ip daddr !="))
                Assert.Contains($"ip daddr {t.Subnet.GatewayIp} ", l);
            Assert.DoesNotContain($"ip daddr {t.Subnet.GuestIp}", l);
        }
    }

    [Fact]
    public void Vpn_ForwardAcceptOnlyViaWgIface_AndMasquerades()
    {
        var r = NftRulesetBuilder.Build(Topo(NetworkMode.LiveVpn));
        var accepts = Chain(r, "forward").Where(l => l.EndsWith(" accept")).ToList();
        Assert.Equal(2, accepts.Count);
        Assert.Equal($"iifname \"{Ns}\" oifname \"{Wg}\" accept", accepts[0]);
        Assert.Equal($"iifname \"{Wg}\" oifname \"{Ns}\" ct state established,related accept", accepts[1]);
        Assert.Contains($"oifname \"{Wg}\" masquerade", Chain(r, "postrouting"));
        Assert.Contains($"oifname \"{Wg}\" accept", Chain(r, "output"));
    }

    [Theory, MemberData(nameof(AllModes))]
    public void Redirects_HttpHttpsToMitm_AndDnsToGateway(NetworkMode mode)
    {
        var t = Topo(mode, mitm: 8123);
        var r = NftRulesetBuilder.Build(t);
        var pre = Chain(r, "prerouting");
        var http = Assert.Single(pre, l => l.Contains("redirect to :8123"));
        Assert.Contains($"iifname \"{Ns}\"", http);
        Assert.Contains("tcp dport { 80, 443 }", http);
        Assert.Contains("ip daddr !=", http);
        foreach (var range in Ranges) Assert.Contains(range, http);
        Assert.Contains($"iifname \"{Ns}\" udp dport 53 dnat ip to {t.Subnet.GatewayIp}", pre);
        Assert.Contains($"iifname \"{Ns}\" tcp dport 53 dnat ip to {t.Subnet.GatewayIp}", pre);
        var input = Chain(r, "input");
        Assert.Contains($"iifname \"{Ns}\" ip daddr {t.Subnet.GatewayIp} tcp dport 8123 ct status dnat accept", input);
        Assert.Contains($"iifname \"{Ns}\" udp dport 67 accept", input);
        Assert.Contains($"iifname \"{Ns}\" ip daddr {t.Subnet.GatewayIp} udp dport 53 accept", input);
        Assert.Contains($"iifname \"{Ns}\" ip daddr {t.Subnet.GatewayIp} tcp dport 53 accept", input);
    }

    [Theory, MemberData(nameof(AllModes))]
    public void Output_OnlyLoopbackAndVeth_PlusWgInVpn(NetworkMode mode)
    {
        var o = Chain(NftRulesetBuilder.Build(Topo(mode)), "output").Where(l => l.EndsWith(" accept")).ToList();
        Assert.Contains("oifname \"lo\" accept", o);
        Assert.Contains($"oifname \"{Ns}\" accept", o);
        Assert.Equal(mode == NetworkMode.LiveVpn ? 3 : 2, o.Count);
    }

    [Fact]
    public void Build_RejectsUnvalidatedInputs()
    {
        var good = Topo(NetworkMode.Simulated);
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { Names = good.Names with { VethNs = "eth0\" accept #" } }));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { Names = good.Names with { WgIface = "mxn0123456789ab" } }));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { Names = good.Names with { Namespace = "mxn0123456789abc" } }));
        foreach (var bad in new[] { "::1", "1", "10.0.0.1/8", "1.2.3.4 accept", "evil", "", "256.1.1.1", " 1.2.3.4" })
            Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(Topo(NetworkMode.None, [bad])));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { SinkholeIp = "198.18.0.10; drop" }));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { Subnet = good.Subnet with { GatewayIp = "10.213.0.1 accept" } }));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { MitmPort = 0 }));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { MitmPort = 70000 }));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { Mode = NetworkMode.LiveTor }));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { Mode = (NetworkMode)99 }));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { ControlPlaneIps = null! }));
    }

    [Theory, MemberData(nameof(AllModes))]
    public void Ruleset_PassesNftSyntaxCheck(NetworkMode mode)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MALDEX_REAL_NET") == "1", "MALDEX_REAL_NET=1 not set; skipping nft -c syntax check.");
        Assert.Equal(0, NftCheck(NftRulesetBuilder.Build(Topo(mode)), out var err));
        Assert.True(err.Length == 0, err);
        Assert.Equal(0, NftCheck(HostGuardRuleset.Build(), out err));
        Assert.True(err.Length == 0, err);
    }

    private static int NftCheck(string ruleset, out string stderr)
    {
        var psi = new ProcessStartInfo("sudo", ["-n", "nft", "-c", "-f", "-"])
        { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(ruleset);
        p.StandardInput.Close();
        stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}

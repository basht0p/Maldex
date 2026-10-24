using Maldex.Core.Domain;
using Maldex.Infrastructure.Net;
using Xunit;

namespace Maldex.Core.Tests.Net;

/// <summary>The rulesets and names of the Direct egress mode (template edit sessions): internet yes; LAN, host, control plane no.</summary>
public class DirectEgressRulesetTests
{
    private static readonly Guid Id = Guid.Parse("0123456789abcdef0123456789abcdef");
    private static readonly string[] Ranges =
        ["0.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10", "127.0.0.0/8", "224.0.0.0/4", "240.0.0.0/4"];
    private static readonly string[] Cp = ["192.168.50.7", "203.0.113.9"];
    private static readonly string[] Dns = ["192.168.3.10", "8.8.8.8"];
    private const string Guest = "mxg0123456789ab";
    private const string UplinkNs = "mxy0123456789ab";
    private const string UplinkHost = "mxu0123456789ab";

    private static EgressTopology Topo(string[]? dns = null, string[]? cp = null, UplinkSubnet? uplink = null) =>
        new(RunNetNames.For(Id), RunSubnet.For(3), NetworkMode.Direct, cp ?? Cp, "198.18.0.10", 8080, null,
            uplink ?? UplinkSubnet.For("100.127.0.0/16", 3), dns ?? Dns);

    private static List<string> Chain(string ruleset, string name)
    {
        var lines = ruleset.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        var start = lines.FindIndex(l => l == $"chain {name} {{");
        Assert.True(start >= 0, $"chain {name} missing");
        var end = lines.FindIndex(start, l => l == "}");
        return lines.Skip(start + 1).Take(end - start - 1).Where(l => l.Length > 0).ToList();
    }

    // ---- names ----

    [Fact]
    public void Names_HaveUplinkPair_AndPerLeaseHostTable()
    {
        var n = RunNetNames.For(Id);
        Assert.Equal(UplinkHost, n.UplinkHost);
        Assert.Equal(UplinkNs, n.UplinkNs);
        Assert.Equal("maldex_direct_0123456789ab", n.DirectTable);
        Assert.True(n.UplinkHost.Length <= 15 && n.UplinkNs.Length <= 15);
        Assert.NotEqual(n.VethHost, n.UplinkNs); // the bridge-side veth is already mxv<hex>
    }

    [Fact]
    public void UplinkSubnet_CarvesDisjointSlash30s_FromThePool()
    {
        var a = UplinkSubnet.For("100.127.0.0/16", 0);
        Assert.Equal(new UplinkSubnet("100.127.0.1", "100.127.0.2", "100.127.0.0/30"), a);
        Assert.Equal(new UplinkSubnet("100.127.0.5", "100.127.0.6", "100.127.0.4/30"), UplinkSubnet.For("100.127.0.0/16", 1));
        Assert.Equal("100.127.1.1", UplinkSubnet.For("100.127.0.0/16", 64).HostIp);
        Assert.Equal(16384, Enumerable.Range(0, 16384).Select(i => UplinkSubnet.For("100.127.0.0/16", i).Cidr).Distinct().Count());
    }

    [Theory]
    [InlineData("100.127.0.0/16", 16384)]
    [InlineData("100.127.0.0/16", -1)]
    [InlineData("100.127.0.0/31", 0)]
    [InlineData("100.127.0.0", 0)]
    [InlineData("not-a-pool", 0)]
    [InlineData("::1/64", 0)]
    [InlineData("100.127.0.5/16", 0)] // not network-aligned
    public void UplinkSubnet_RejectsBadPoolOrIndex(string pool, int index) =>
        Assert.Throws<ArgumentException>(() => UplinkSubnet.For(pool, index));

    // ---- the run namespace ----

    [Fact]
    public void Namespace_Direct_ForwardsOnlyGuestToUplink_AndKeepsEveryDrop()
    {
        var r = NftRulesetBuilder.Build(Topo());
        foreach (var c in new[] { "input", "forward", "output" }) Assert.Contains("policy drop;", Chain(r, c)[0]);

        var fwd = Chain(r, "forward");
        var drops = fwd.Where(l => l.EndsWith(" drop") && l.Contains("daddr")).ToList();
        Assert.NotEmpty(drops);
        var joined = string.Join('\n', drops);
        foreach (var range in Ranges) Assert.Contains(range, joined);
        foreach (var ip in Cp) Assert.Contains(ip, joined);
        var accepts = fwd.Where(l => l.EndsWith(" accept")).ToList();
        Assert.Equal(
            [$"iifname \"{Guest}\" oifname \"{UplinkNs}\" accept", $"iifname \"{UplinkNs}\" oifname \"{Guest}\" ct state established,related accept"],
            accepts);
        Assert.True(fwd.FindLastIndex(l => drops.Contains(l)) < fwd.FindIndex(l => l.EndsWith(" accept")), "all drops precede the accepts");
    }

    [Fact]
    public void Namespace_Direct_DnsServersAreTheOnlyPrivateException_Tcp53AndUdp53_FromThisNamespaceOnly()
    {
        var r = NftRulesetBuilder.Build(Topo());
        var output = Chain(r, "output");
        Assert.Equal(
        [
            "oifname \"lo\" accept",
            $"oifname \"{Guest}\" accept",
            $"oifname \"{UplinkNs}\" ip daddr {{ 192.168.3.10, 8.8.8.8 }} udp dport 53 accept",
            $"oifname \"{UplinkNs}\" ip daddr {{ 192.168.3.10, 8.8.8.8 }} tcp dport 53 accept",
        ], output.Skip(1).Where(l => !l.StartsWith("meta nfproto")).ToList());
        // the guest's own packets are never allowed to the DNS servers (they are redirected to the gateway's dnsmasq), and the
        // forward chain has no DNS exception at all
        Assert.DoesNotContain(Chain(r, "forward"), l => l.Contains("192.168.3.10") && l.EndsWith("accept"));
        Assert.Contains($"iifname \"{Guest}\" udp dport 53 dnat ip to {RunSubnet.For(3).GatewayIp}", Chain(r, "prerouting"));
    }

    [Fact]
    public void Namespace_Direct_NothingIsAcceptedFromTheUplink_ExceptReplies()
    {
        var r = NftRulesetBuilder.Build(Topo());
        Assert.DoesNotContain(Chain(r, "input"), l => l.Contains(UplinkNs) && !l.Contains("established"));
        Assert.Contains("ct state established,related accept", Chain(r, "input"));
        Assert.DoesNotContain(Chain(r, "input"), l => l.Contains("ip daddr !="));
    }

    [Fact]
    public void Namespace_Direct_NoInterception_MasqueradeOnTheUplinkOnly()
    {
        var r = NftRulesetBuilder.Build(Topo());
        Assert.DoesNotContain("redirect", r);
        Assert.DoesNotContain("8080", r);
        Assert.Equal([$"oifname \"{UplinkNs}\" masquerade"], Chain(r, "postrouting").Skip(1).ToList());
        Assert.Equal(2, Chain(r, "prerouting").Count(l => l.Contains("dnat ip to")));
        Assert.Equal(2, Chain(r, "prerouting").Skip(1).Count());
    }

    [Fact]
    public void Namespace_Direct_NamesAreValidated_AndTheSetupIsComplete()
    {
        var good = Topo();
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { Uplink = null }));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { DirectDnsServers = null }));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { DirectDnsServers = [] }));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { Names = good.Names with { UplinkNs = "mxy_bad" } }));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { DirectDnsServers = ["1.1.1.1; flush ruleset"] }));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { DirectDnsServers = ["127.0.0.53"] }));
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { DirectDnsServers = ["192.168.50.7"] })); // a control-plane IP
        Assert.Throws<ArgumentException>(() => NftRulesetBuilder.Build(good with { Uplink = new UplinkSubnet("100.127.0.1", "oops", "x") }));
    }

    [Fact]
    public void Namespace_NonDirectModes_AreUnchanged_ByTheUplinkFields()
    {
        var plain = new EgressTopology(RunNetNames.For(Id), RunSubnet.For(3), NetworkMode.None, Cp, "198.18.0.10", 8080);
        Assert.Equal(NftRulesetBuilder.Build(plain), NftRulesetBuilder.Build(plain with { Uplink = UplinkSubnet.For("100.127.0.0/16", 3), DirectDnsServers = Dns }));
        Assert.DoesNotContain("masquerade", NftRulesetBuilder.Build(plain));
    }

    // ---- the host table ----

    [Fact]
    public void Host_Table_ForwardsOnlyUplinkToTheDefaultInterface_WithMasquerade_AndNeverAcceptsInput()
    {
        var t = DirectUplinkRuleset.Build(RunNetNames.For(Id), "wlp0s20f3", Cp, Dns);
        Assert.Contains("delete table inet maldex_direct_0123456789ab", t);
        Assert.All(System.Text.RegularExpressions.Regex.Matches(t, @"table inet (\S+)"), m => Assert.Equal("maldex_direct_0123456789ab", m.Groups[1].Value)); // only this lease's table
        Assert.DoesNotContain("flush", t);
        Assert.DoesNotContain("table ip ", t);
        Assert.DoesNotContain("maldex_guard", t);

        // never accepts anything toward the host
        var input = Chain(t, "input");
        Assert.Equal([$"iifname \"{UplinkHost}\" drop"], input.Skip(1).ToList());

        var fwd = Chain(t, "forward");
        Assert.Contains("policy accept;", fwd[0]); // the host's other traffic is not this table's business
        var accepts = fwd.Where(l => l.EndsWith(" accept")).ToList();
        Assert.All(accepts, l => Assert.Contains(UplinkHost, l));
        Assert.Contains($"iifname \"{UplinkHost}\" oifname \"wlp0s20f3\" accept", accepts);
        Assert.Contains($"oifname \"{UplinkHost}\" iifname \"wlp0s20f3\" ct state established,related accept", accepts);
        Assert.Equal($"iifname \"{UplinkHost}\" drop", fwd.Single(l => l == $"iifname \"{UplinkHost}\" drop"));
        Assert.Equal($"oifname \"{UplinkHost}\" drop", fwd.Single(l => l == $"oifname \"{UplinkHost}\" drop"));

        var nat = Chain(t, "postrouting");
        Assert.Equal([$"iifname \"{UplinkHost}\" oifname \"wlp0s20f3\" masquerade"], nat.Skip(1).ToList());
    }

    [Fact]
    public void Host_Table_DropsPrivateAndControlPlane_ExceptDnsServersOnPort53_BeforeTheGeneralAccept()
    {
        var t = DirectUplinkRuleset.Build(RunNetNames.For(Id), "eth0", Cp, Dns);
        var fwd = Chain(t, "forward");
        var general = fwd.IndexOf($"iifname \"{UplinkHost}\" oifname \"eth0\" accept");
        var dnsUdp = fwd.FindIndex(l => l.Contains("udp dport 53 accept"));
        var dnsTcp = fwd.FindIndex(l => l.Contains("tcp dport 53 accept"));
        var drop = fwd.FindIndex(l => l.Contains("ip daddr") && l.EndsWith(" drop"));
        Assert.True(dnsUdp >= 0 && dnsTcp >= 0 && drop > dnsUdp && drop > dnsTcp && general > drop, string.Join('\n', fwd));
        Assert.Contains("ip daddr { 192.168.3.10, 8.8.8.8 }", fwd[dnsUdp]);
        Assert.Contains("ip daddr { 192.168.3.10, 8.8.8.8 }", fwd[dnsTcp]);
        foreach (var range in Ranges) Assert.Contains(range, fwd[drop]);
        foreach (var ip in Cp) Assert.Contains(ip, fwd[drop]);
        Assert.Contains($"iifname \"{UplinkHost}\" meta nfproto != ipv4 drop", fwd);
    }

    [Theory]
    [InlineData("")]
    [InlineData("eth0\" ; flush ruleset ; \"")]
    [InlineData("wlan*")]
    [InlineData("a b")]
    [InlineData("0123456789abcdef")]
    [InlineData("mxb0123456789ab")]
    [InlineData("lo")]
    public void Host_Table_RejectsUnsafeOrWrongDefaultInterfaces(string iface) =>
        Assert.Throws<ArgumentException>(() => DirectUplinkRuleset.Build(RunNetNames.For(Id), iface, Cp, Dns));

    [Fact]
    public void Host_Table_RejectsBadAddresses()
    {
        var n = RunNetNames.For(Id);
        Assert.Throws<ArgumentException>(() => DirectUplinkRuleset.Build(n, "eth0", ["1.2.3"], Dns));
        Assert.Throws<ArgumentException>(() => DirectUplinkRuleset.Build(n, "eth0", Cp, ["8.8.8.8/32"]));
        Assert.Throws<ArgumentException>(() => DirectUplinkRuleset.Build(n, "eth0", Cp, []));
        Assert.Throws<ArgumentException>(() => DirectUplinkRuleset.Build(n, "eth0", Cp, ["192.168.50.7"]));
        Assert.Throws<ArgumentException>(() => DirectUplinkRuleset.Build(n, "eth0", Cp, ["127.0.0.1"]));
    }

    // ---- the host guard ----

    [Fact]
    public void HostGuard_LetsUplinkTrafficReachTheDirectTable_ButStillDropsInputFromEveryMxInterface()
    {
        var guard = HostGuardRuleset.Build();
        var inet = guard[guard.IndexOf("table inet maldex_guard {", StringComparison.Ordinal)..guard.IndexOf("table bridge maldex_guard {", StringComparison.Ordinal)];
        var fwd = Chain(inet, "forward");
        var upOut = fwd.IndexOf("iifname \"mxu*\" oifname != \"mx*\" accept");
        var upBack = fwd.IndexOf("oifname \"mxu*\" iifname != \"mx*\" ct state established,related accept");
        Assert.True(upOut >= 0 && upBack >= 0, string.Join('\n', fwd));
        Assert.True(upOut < fwd.IndexOf("iifname \"mx*\" drop") && upBack < fwd.IndexOf("oifname \"mx*\" drop"));
        Assert.Equal(["type filter hook input priority -10; policy accept;", "iifname \"mx*\" drop"], Chain(inet, "input"));
    }
}

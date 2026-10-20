using Maldex.Infrastructure.Net;
using Xunit;

namespace Maldex.Core.Tests.Net;

public class HostGuardRulesetTests
{
    private static string Norm(string s) =>
        string.Join('\n', s.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim();

    [Fact]
    public void HostGuard_DropsForwardingForMxInterfaces()
    {
        var t = HostGuardRuleset.Build();
        Assert.Contains("table inet maldex_guard {", t);
        Assert.Contains("table bridge maldex_guard {", t);
        Assert.Contains("delete table inet maldex_guard", t);
        Assert.Contains("delete table bridge maldex_guard", t);
        Assert.Equal(2, t.Split("type filter hook forward priority -10; policy accept;").Length - 1);
        Assert.Contains("iifname \"mx*\" drop", t);
        Assert.Contains("oifname \"mx*\" drop", t);
        Assert.Contains("type filter hook input priority -10; policy accept;", t);
        Assert.Contains("ibrname \"mxb*\" ether type != { ip, arp } drop", t);
    }

    [Fact]
    public void HostGuard_BridgeTable_DropsLocalDeliveryFromRunBridges()
    {
        var t = HostGuardRuleset.Build();
        var bridge = t[t.IndexOf("table bridge maldex_guard {", StringComparison.Ordinal)..];
        Assert.Contains("chain input {", bridge);
        Assert.Contains("ibrname \"mxb*\" drop", bridge);
    }

    [Fact]
    public void HostGuard_AssumesBrNetfilterIsNotLoaded()
    {
        // The guard filters bridged L2 frames in the 'bridge' family. If br_netfilter were loaded (and
        // bridge-nf-call-iptables=1), bridged IP traffic would instead traverse the inet hooks and the 'ibrname'
        // matches here, so the guard assumes it is NOT loaded; build.sh real_verify enforces that.
        var t = HostGuardRuleset.Build();
        Assert.Contains("table bridge maldex_guard", t);
        Assert.DoesNotContain("br_netfilter", t);
        Assert.DoesNotContain("meta ibrname", t); // inet-family bridge-port matching is not relied on
        Assert.Contains("br_netfilter", File.ReadAllText(FindRepoFile("build.sh")));
    }

    private static string FindRepoFile(string rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, rel))) return Path.Combine(d.FullName, rel);
        throw new FileNotFoundException(rel);
    }

    [Fact]
    public void HostGuard_FileMatchesBuilder()
    {
        string? path = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "deploy", "host", "maldex-host-guard.nft");
            if (File.Exists(c)) { path = c; break; }
        }
        Assert.NotNull(path);
        Assert.Equal(Norm(File.ReadAllText(path!)), Norm(HostGuardRuleset.Build()));
    }
}

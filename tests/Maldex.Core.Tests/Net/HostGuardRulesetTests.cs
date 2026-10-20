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

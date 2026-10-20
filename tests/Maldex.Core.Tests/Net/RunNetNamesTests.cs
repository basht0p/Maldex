using System.Net;
using Maldex.Infrastructure.Net;
using Xunit;

namespace Maldex.Core.Tests.Net;

public class RunNetNamesTests
{
    private static readonly Guid Id = Guid.Parse("0123456789abcdef0123456789abcdef");

    [Fact]
    public void RunNetNames_AreAtMost15Chars_AndDeterministic()
    {
        var n = RunNetNames.For(Id);
        Assert.Equal(n, RunNetNames.For(Id));
        Assert.Equal("mxb0123456789ab", n.Bridge);
        Assert.Equal("mxt0123456789ab", n.Tap);
        Assert.Equal("mxv0123456789ab", n.VethHost);
        Assert.Equal("mxg0123456789ab", n.VethNs);
        Assert.Equal("mxn0123456789ab", n.Namespace);
        Assert.Equal("mxw0123456789ab", n.WgIface);
        foreach (var s in new[] { n.Bridge, n.Tap, n.VethHost, n.VethNs, n.Namespace, n.WgIface })
            Assert.True(s.Length <= 15, s);
        Assert.NotEqual(n, RunNetNames.For(Guid.NewGuid()));
    }

    [Fact]
    public void GuestMac_IsDeterministicQemuUnicast_FromTaskId()
    {
        Assert.Equal("52:54:00:01:23:45", RunNetNames.GuestMac(Id));
        Assert.Equal(RunNetNames.GuestMac(Id), RunNetNames.GuestMac(Id));
        Assert.Matches("^52:54:00(:[0-9a-f]{2}){3}$", RunNetNames.GuestMac(Guid.NewGuid()));
    }

    [Fact]
    public void RunSubnet_IndexesDoNotOverlap()
    {
        var seen = new HashSet<uint>();
        for (var i = 0; i < 64; i++)
        {
            var s = RunSubnet.For(i);
            Assert.Equal(i, s.Index);
            var gw = Num(s.GatewayIp);
            var guest = Num(s.GuestIp);
            Assert.Equal(gw + 1, guest);
            Assert.Equal(1u, gw % 4);
            Assert.Equal(0x0AD50000u, gw & 0xFFFF0000u); // 10.213.0.0/16
            Assert.True(seen.Add(gw & ~3u));
            Assert.EndsWith("/30", s.Cidr);
        }
        Assert.Equal(64, seen.Count);
        Assert.Equal("10.213.0.0/30", RunSubnet.For(0).Cidr);
        Assert.Equal("10.213.0.1", RunSubnet.For(0).GatewayIp);
        Assert.Equal("10.213.0.2", RunSubnet.For(0).GuestIp);
        Assert.Equal("10.213.0.252/30", RunSubnet.For(63).Cidr);
        Assert.Equal("10.213.1.0/30", RunSubnet.For(64).Cidr);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16384)]
    public void RunSubnet_RejectsOutOfRange(int i) => Assert.Throws<ArgumentOutOfRangeException>(() => RunSubnet.For(i));

    private static uint Num(string ip)
    {
        var b = IPAddress.Parse(ip).GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }
}

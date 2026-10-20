using Maldex.Core.Domain;

namespace Maldex.Infrastructure.Net;

/// <summary>Per-run interface and namespace names. Each is at most 15 characters (the Linux IFNAMSIZ limit).</summary>
public sealed record RunNetNames(string Bridge, string Tap, string VethHost, string VethNs, string Namespace, string WgIface)
{
    public static RunNetNames For(Guid taskId)
    {
        var h = taskId.ToString("N")[..12];
        return new($"mxb{h}", $"mxt{h}", $"mxv{h}", $"mxg{h}", $"mxn{h}", $"mxw{h}");
    }
}

/// <summary>A /30 carved from 10.213.0.0/16: gateway .1, guest .2.</summary>
public sealed record RunSubnet(int Index, string GatewayIp, string GuestIp, string Cidr)
{
    public const int MaxIndex = 16383; // 16384 /30 blocks in a /16

    public static RunSubnet For(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, MaxIndex);
        var third = index / 64;
        var fourth = index % 64 * 4;
        return new(index, $"10.213.{third}.{fourth + 1}", $"10.213.{third}.{fourth + 2}", $"10.213.{third}.{fourth}/30");
    }
}

public sealed record EgressTopology(
    RunNetNames Names, RunSubnet Subnet, NetworkMode Mode,
    IReadOnlyList<string> ControlPlaneIps, string SinkholeIp, int MitmPort, string? VpnDnsIp = null);

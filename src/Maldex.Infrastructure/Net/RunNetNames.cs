using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Maldex.Core.Domain;

namespace Maldex.Infrastructure.Net;

/// <summary>Per-run interface and namespace names. Each is at most 15 characters (the Linux IFNAMSIZ limit).</summary>
public sealed record RunNetNames(string Bridge, string Tap, string VethHost, string VethNs, string Namespace, string WgIface, string UplinkHost, string UplinkNs)
{
    public static RunNetNames For(Guid taskId)
    {
        var h = taskId.ToString("N")[..12];
        return new($"mxb{h}", $"mxt{h}", $"mxv{h}", $"mxg{h}", $"mxn{h}", $"mxw{h}", $"mxu{h}", $"mxy{h}");
    }

    /// <summary>The host nft table of a <see cref="NetworkMode.Direct"/> lease (one per lease, so concurrent leases never share state).</summary>
    public string DirectTable => "maldex_direct_" + Namespace[3..];

    /// <summary>
    /// The guest NIC's MAC: QEMU's 52:54:00 prefix + the first 3 bytes of the task id. Deterministic, so every VM attempt
    /// of a run (including the agent-ready retry) presents the MAC the run's dnsmasq has a <c>--dhcp-host</c> reservation for.
    /// Only unique per bridge, which is all it needs (one guest per run bridge).
    /// </summary>
    public static string GuestMac(Guid taskId)
    {
        var h = taskId.ToString("N");
        return $"52:54:00:{h[..2]}:{h[2..4]}:{h[4..6]}";
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

/// <summary>
/// A /30 carved from the Direct uplink pool (default 100.127.0.0/16) at the lease's slot index: host end .1, namespace end .2.
/// </summary>
public sealed record UplinkSubnet(string HostIp, string NsIp, string Cidr)
{
    public static UplinkSubnet For(string pool, int index)
    {
        var parts = (pool ?? "").Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var net) || net.AddressFamily != AddressFamily.InterNetwork || net.ToString() != parts[0]
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix is < 1 or > 30)
            throw new ArgumentException("The Direct uplink pool must be an IPv4 CIDR with a prefix from /1 to /30.", nameof(pool));
        var b = net.GetAddressBytes();
        var baseAddr = (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
        var blocks = 1L << (30 - prefix);
        if ((baseAddr & ~(uint.MaxValue << (32 - prefix))) != 0) throw new ArgumentException("The Direct uplink pool is not network-aligned.", nameof(pool));
        if (index < 0 || index >= blocks) throw new ArgumentException("The Direct uplink pool has no /30 left for this slot.", nameof(index));
        var start = baseAddr + (uint)index * 4;
        static string Dotted(uint v) => $"{v >> 24}.{v >> 16 & 255}.{v >> 8 & 255}.{v & 255}";
        return new(Dotted(start + 1), Dotted(start + 2), Dotted(start) + "/30");
    }
}

public sealed record EgressTopology(
    RunNetNames Names, RunSubnet Subnet, NetworkMode Mode,
    IReadOnlyList<string> ControlPlaneIps, string SinkholeIp, int MitmPort, string? VpnDnsIp = null,
    UplinkSubnet? Uplink = null, IReadOnlyList<string>? DirectDnsServers = null);

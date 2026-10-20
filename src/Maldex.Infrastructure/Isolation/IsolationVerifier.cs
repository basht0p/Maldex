using System.Net;
using System.Net.Sockets;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Net;

namespace Maldex.Infrastructure.Isolation;

/// <summary>The outcome of the pre-detonation self-test. <see cref="Reason"/> is set whenever <see cref="Isolated"/> is false.</summary>
public sealed record IsolationVerdict(bool Isolated, string? Reason)
{
    public static IsolationVerdict Pass { get; } = new(true, null);

    public static IsolationVerdict Fail(string reason) => new(false, reason);
}

/// <summary>What the coordinator asks the guest to probe for one run, and the probe names it expects back.</summary>
public sealed record IsolationProbePlan(IReadOnlyList<ProbeTarget> Targets, string EchoUrl)
{
    public IReadOnlyList<string> ExpectedNames => Targets.Select(t => t.Name).ToList();

    private static readonly int[] GatewayPorts = [22, 80, 135, 139, 443, 445, 3389, 5000, 5900, 8080, 8443];
    private static readonly int[] ControlPlanePorts = [22, 80, 443, 445, 3389, 5000, 5001, 8080];
    private static readonly int[] WebPorts = [80, 443];

    public static IsolationProbePlan Build(NetOptions net, RunNetworkInfo run)
    {
        // 80/443/8080 to the gateway are NOT redirected (the redirect only applies to non-private destinations), so they must be unreachable.
        var targets = new List<ProbeTarget> { new("gateway", run.GatewayIp, GatewayPorts) };
        for (var i = 0; i < net.ControlPlaneIps.Length; i++)
            targets.Add(new ProbeTarget($"control_plane_{i}", net.ControlPlaneIps[i], ControlPlanePorts));
        targets.Add(new ProbeTarget("rfc1918_10", "10.0.0.1", WebPorts));
        targets.Add(new ProbeTarget("rfc1918_172", "172.16.0.1", WebPorts));
        targets.Add(new ProbeTarget("rfc1918_192", "192.168.1.1", WebPorts));
        targets.Add(new ProbeTarget("link_local", "169.254.169.254", WebPorts));
        return new IsolationProbePlan(targets, net.EchoUrl);
    }
}

/// <summary>
/// Decides whether the guest's self-test answer proves the run's isolation. Nothing passes by default: every check must
/// positively succeed, and anything missing, duplicated, unexpected or unparseable is a failure.
/// </summary>
public static class IsolationVerifier
{
    public static IsolationVerdict Verify(NetworkMode mode, IReadOnlyList<string> expectedProbeNames, string? expectedEgressIp, SelfTestResultMessage? result)
    {
        if (result is null) return IsolationVerdict.Fail("no self-test result");
        if (expectedProbeNames is null || expectedProbeNames.Count == 0) return IsolationVerdict.Fail("no probes were expected");
        if (result.Probes is null) return IsolationVerdict.Fail("self-test result has no probes");

        var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var p in result.Probes)
        {
            if (p is null || string.IsNullOrEmpty(p.Name)) return IsolationVerdict.Fail("self-test result has an unnamed probe");
            if (!seen.TryAdd(p.Name, p.Reachable)) return IsolationVerdict.Fail($"probe '{Show(p.Name)}' reported more than once");
        }

        var expected = new HashSet<string>(expectedProbeNames, StringComparer.Ordinal);
        foreach (var name in seen.Keys)
            if (!expected.Contains(name)) return IsolationVerdict.Fail($"unexpected probe '{Show(name)}'");
        foreach (var name in expected)
        {
            if (!seen.TryGetValue(name, out var reachable)) return IsolationVerdict.Fail($"probe '{name}' is missing");
            if (reachable) return IsolationVerdict.Fail($"{name} is reachable from the guest");
        }

        var echo = result.EchoBody;
        switch (mode)
        {
            case NetworkMode.None:
                return echo is null ? IsolationVerdict.Pass : IsolationVerdict.Fail("the guest reached the internet with networking off");

            case NetworkMode.Simulated:
                // INetSim answers with a page; a bare public IP means the request reached the real internet.
                return echo is not null && TryParseIp(echo, out var ip) && IsPublic(ip)
                    ? IsolationVerdict.Fail("the guest reached the real internet in simulated mode")
                    : IsolationVerdict.Pass;

            case NetworkMode.LiveVpn:
                if (!TryParseIp(expectedEgressIp, out var want)) return IsolationVerdict.Fail("the expected VPN egress IP is unknown");
                if (echo is null || !TryParseIp(echo, out var got)) return IsolationVerdict.Fail("the guest could not confirm its egress IP");
                return Normalize(got).Equals(Normalize(want))
                    ? IsolationVerdict.Pass
                    : IsolationVerdict.Fail($"the egress IP {got} is not the VPN exit {want}");

            default:
                return IsolationVerdict.Fail($"no isolation check exists for network mode {mode}");
        }
    }

    private static string Show(string s) => s.Length <= 64 ? s : s[..64];

    private static bool TryParseIp(string? s, out IPAddress ip)
    {
        ip = IPAddress.None;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (t.Length > 45 || !IPAddress.TryParse(t, out var parsed) || parsed is null) return false;
        ip = parsed;
        return true;
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>True for anything routable on the internet. Documentation ranges count as public on purpose (fail closed).</summary>
    internal static bool IsPublic(IPAddress address)
    {
        var ip = Normalize(address);
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b6 = ip.GetAddressBytes();
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
            return (b6[0] & 0xFE) != 0xFC; // unique local fc00::/7
        }

        var b = ip.GetAddressBytes();
        return !(b[0] == 0
            || b[0] == 10
            || (b[0] == 100 && b[1] is >= 64 and <= 127)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 198 && b[1] is 18 or 19) // 198.18.0.0/15: the fake sinkhole range
            || b[0] >= 224);
    }
}

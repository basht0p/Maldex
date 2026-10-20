using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Maldex.Core.Abstractions;

namespace Maldex.Infrastructure.Net;

/// <summary>
/// A parsed WireGuard client config (wg-quick format). <see cref="SetConfText"/> contains only keys that
/// <c>wg setconf</c> accepts (it carries the private key: never log it). Task 13 reuses this for the expected egress IP.
/// </summary>
public sealed record WireGuardConf(string Address, string SetConfText, string Dns, string? EgressIp)
{
    private static readonly HashSet<string> WgQuickOnly = new(StringComparer.OrdinalIgnoreCase)
        { "Address", "DNS", "MTU", "Table", "PreUp", "PostUp", "PreDown", "PostDown", "SaveConfig" };

    private static readonly Regex EgressComment =
        new(@"^#\s*MaldexEgressIp\s*=\s*(\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <exception cref="EgressUnavailableException">Address or DNS (IPv4) is missing or malformed.</exception>
    public static WireGuardConf Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? address = null, dns = null, egress = null;
        var kept = new StringBuilder();
        var peerHasKeepalive = false;
        var inPeer = false;

        // A keepalive makes the kernel send a packet as soon as the interface comes up, which starts the handshake
        // without needing any guest-facing filter hole (our ruleset only lets DNS reach a private VPN resolver).
        void EndPeer()
        {
            if (inPeer && !peerHasKeepalive) kept.Append("PersistentKeepalive = 25\n");
            peerHasKeepalive = false;
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '#')
            {
                var m = EgressComment.Match(line);
                if (m.Success && IsIpv4(m.Groups[1].Value)) egress = m.Groups[1].Value;
                continue;
            }
            if (line[0] == '[')
            {
                EndPeer();
                var section = line.Trim('[', ']').Trim();
                inPeer = section.Equals("Peer", StringComparison.OrdinalIgnoreCase);
                kept.Append('[').Append(section).Append("]\n");
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Equals("Address", StringComparison.OrdinalIgnoreCase))
                address ??= value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(IsIpv4Cidr);
            else if (key.Equals("DNS", StringComparison.OrdinalIgnoreCase))
                dns ??= value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(IsIpv4);
            if (WgQuickOnly.Contains(key)) continue;
            if (inPeer && key.Equals("PersistentKeepalive", StringComparison.OrdinalIgnoreCase)) peerHasKeepalive = true;
            kept.Append(key).Append(" = ").Append(value).Append('\n');
        }
        EndPeer();

        if (address is null) throw new EgressUnavailableException("WireGuard config has no IPv4 Address.");
        if (dns is null) throw new EgressUnavailableException("WireGuard config has no IPv4 DNS; refusing to resolve outside the tunnel.");
        return new WireGuardConf(address, kept.ToString(), dns, egress);
    }

    private static bool IsIpv4(string v) =>
        IPAddress.TryParse(v, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() == v;

    private static bool IsIpv4Cidr(string v)
    {
        var parts = v.Split('/');
        return parts.Length == 2 && IsIpv4(parts[0]) && int.TryParse(parts[1], out var len) && len is >= 0 and <= 32;
    }
}

/// <summary>Builds privileged command lines: always <c>sudo -n</c> + discrete arguments, never a shell string.</summary>
public static class PrivilegedCommands
{
    public static (string File, IReadOnlyList<string> Args) Sudo(params string[] args) => ("sudo", ["-n", .. args]);

    /// <summary><c>sudo -n ip netns exec {ns} args...</c></summary>
    public static (string File, IReadOnlyList<string> Args) InNs(string ns, params string[] args) =>
        Sudo(["ip", "netns", "exec", ns, .. args]);

    /// <summary>
    /// Detached daemon inside a namespace: <c>sudo -n setsid -f sh -c 'exec "$@" ...' log ip netns exec ns args...</c>.
    /// The fixed script only redirects stdio (so the caller's pipes are not held open); every value is a positional argument.
    /// </summary>
    public static (string File, IReadOnlyList<string> Args) DetachedInNs(string ns, string logPath, params string[] args) =>
        Sudo(["setsid", "-f", "sh", "-c", "exec \"$@\" </dev/null >>\"$0\" 2>&1", logPath, "ip", "netns", "exec", ns, .. args]);
}

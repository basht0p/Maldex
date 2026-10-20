using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Maldex.Core.Domain;

namespace Maldex.Infrastructure.Net;

/// <summary>
/// Builds the <c>nft -f</c> script applied inside a run's network namespace. Default-drop everywhere; every
/// string interpolated into the script is validated first, so no ruleset is ever emitted from unvalidated text.
/// </summary>
public static class NftRulesetBuilder
{
    /// <summary>Never reachable by a guest, and never an accepted input destination other than the gateway.</summary>
    private static readonly string[] PrivateRanges =
        ["0.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10", "127.0.0.0/8", "224.0.0.0/4", "240.0.0.0/4"];

    public static string Build(EgressTopology t)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(t.Names);
        ArgumentNullException.ThrowIfNull(t.Subnet);
        if (t.ControlPlaneIps is null) throw new ArgumentException("ControlPlaneIps must not be null.", nameof(t));
        if (t.Mode is not (NetworkMode.None or NetworkMode.Simulated or NetworkMode.LiveVpn))
            throw new ArgumentException($"Network mode {t.Mode} has no namespace ruleset.", nameof(t));

        var ns = Name(t.Names.VethNs, "mxg", nameof(t.Names.VethNs));
        var wg = Name(t.Names.WgIface, "mxw", nameof(t.Names.WgIface));
        Name(t.Names.Bridge, "mxb", nameof(t.Names.Bridge));
        Name(t.Names.Tap, "mxt", nameof(t.Names.Tap));
        Name(t.Names.VethHost, "mxv", nameof(t.Names.VethHost));
        Name(t.Names.Namespace, "mxn", nameof(t.Names.Namespace));
        var gw = Ip(t.Subnet.GatewayIp, nameof(t.Subnet.GatewayIp));
        Ip(t.Subnet.GuestIp, nameof(t.Subnet.GuestIp));
        Ip(t.SinkholeIp, nameof(t.SinkholeIp));
        if (t.MitmPort is < 1 or > 65535) throw new ArgumentException("MitmPort out of range.", nameof(t));
        var cp = t.ControlPlaneIps.Select(ip => Ip(ip, "ControlPlaneIps")).Distinct().ToList();
        var dns = t.VpnDnsIp is null ? null : Ip(t.VpnDnsIp, nameof(t.VpnDnsIp));
        if (dns is not null && cp.Contains(dns)) throw new ArgumentException("VpnDnsIp must not be a control-plane IP.", nameof(t));

        // Everything a guest must never reach: private ranges (incl. the control plane's own LAN) and the control-plane IPs.
        var blocked = string.Join(", ", PrivateRanges.Concat(cp));
        var vpn = t.Mode == NetworkMode.LiveVpn;
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append('\n');

        L("table inet maldex {}");
        L("delete table inet maldex");
        L("table inet maldex {");

        L("\tchain input {");
        L("\t\ttype filter hook input priority 0; policy drop;");
        L("\t\tmeta nfproto ipv6 drop");
        L("\t\tct state established,related accept");
        L("\t\tiifname \"lo\" accept");
        // Every path to the MITM port must have been DNAT'd/redirected; a direct hit (e.g. via the Simulated any-IP route) is dropped.
        L($"\t\tiifname \"{ns}\" tcp dport {t.MitmPort} ct status & dnat == 0 drop");
        L($"\t\tiifname \"{ns}\" udp dport 67 accept");
        L($"\t\tiifname \"{ns}\" ip daddr {gw} udp dport 53 accept");
        L($"\t\tiifname \"{ns}\" ip daddr {gw} tcp dport 53 accept");
        L($"\t\tiifname \"{ns}\" ip daddr {gw} tcp dport {t.MitmPort} ct status dnat accept");
        if (t.Mode == NetworkMode.Simulated)
        {
            // INetSim answers on any public address; never to the control plane or private ranges (which include the gateway).
            L($"\t\tiifname \"{ns}\" ip daddr != {{ {blocked} }} ct status & dnat == 0 meta l4proto {{ tcp, udp }} accept");
        }
        L("\t}");

        L("\tchain forward {");
        L("\t\ttype filter hook forward priority 0; policy drop;");
        L("\t\tmeta nfproto ipv6 drop");
        if (cp.Count > 0) L($"\t\tip daddr {{ {string.Join(", ", cp)} }} drop");
        // The guest's own address sits inside 10.0.0.0/8, so replies toward the guest leg are exempt from the
        // range drop; they are admitted only by the established/related accept below (LiveVpn), never by anything else.
        L($"\t\toifname != \"{ns}\" ip daddr {{ {string.Join(", ", PrivateRanges)} }} drop");
        if (vpn)
        {
            L($"\t\tiifname \"{ns}\" oifname \"{wg}\" accept");
            L($"\t\tiifname \"{wg}\" oifname \"{ns}\" ct state established,related accept");
        }
        L("\t}");

        L("\tchain output {");
        L("\t\ttype filter hook output priority 0; policy drop;");
        L("\t\tmeta nfproto ipv6 drop");
        L("\t\toifname \"lo\" accept");
        L($"\t\toifname \"{ns}\" accept");
        if (vpn)
        {
            if (dns is not null)
            {
                L($"\t\toifname \"{wg}\" ip daddr {dns} udp dport 53 accept");
                L($"\t\toifname \"{wg}\" ip daddr {dns} tcp dport 53 accept");
            }
            L($"\t\toifname \"{wg}\" ip daddr {{ {blocked} }} drop");
            L($"\t\toifname \"{wg}\" accept");
        }
        L("\t}");

        L("\tchain prerouting {");
        L("\t\ttype nat hook prerouting priority dstnat; policy accept;");
        L($"\t\tiifname \"{ns}\" tcp dport {{ 80, 443 }} ip daddr != {{ {blocked} }} redirect to :{t.MitmPort}");
        L($"\t\tiifname \"{ns}\" udp dport 53 dnat ip to {gw}");
        L($"\t\tiifname \"{ns}\" tcp dport 53 dnat ip to {gw}");
        L("\t}");

        L("\tchain postrouting {");
        L("\t\ttype nat hook postrouting priority srcnat; policy accept;");
        if (vpn) L($"\t\toifname \"{wg}\" masquerade");
        L("\t}");

        L("}");
        return sb.ToString();
    }

    private static string Name(string? value, string prefix, string what)
    {
        if (value is null || !Regex.IsMatch(value, "^" + prefix + "[0-9a-f]{12}$", RegexOptions.None, TimeSpan.FromSeconds(1)))
            throw new ArgumentException($"{what} is not a valid {prefix}<12 hex> interface name.");
        return value;
    }

    /// <summary>Canonical dotted-quad IPv4 only (rejects "1", CIDR, IPv6, whitespace and trailing text).</summary>
    private static string Ip(string? value, string what)
    {
        if (value is null || !IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != value)
            throw new ArgumentException($"{what} is not a valid IPv4 address.");
        return value;
    }
}

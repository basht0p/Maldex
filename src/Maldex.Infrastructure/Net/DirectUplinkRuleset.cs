using System.Text;
using System.Text.RegularExpressions;
using Maldex.Core.Domain;

namespace Maldex.Infrastructure.Net;

/// <summary>
/// The host-side nft table of one <see cref="NetworkMode.Direct"/> lease (<see cref="RunNetNames.DirectTable"/>): the host forwards
/// only that lease's <c>mxu*</c> uplink to the default-route interface, with masquerade, and never accepts input from it. The
/// table has no policy over other traffic (its base chains are <c>policy accept</c>), so it never changes what the host does for
/// anyone else, and it is removed with the lease. It is defence in depth behind the namespace ruleset: the private, link-local and
/// control-plane drops are repeated here, with the same DNS-server exception (TCP/UDP 53 only).
/// </summary>
public static partial class DirectUplinkRuleset
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,14}$")] private static partial Regex Iface();

    public static string Build(RunNetNames names, string defaultIface, IReadOnlyList<string> controlPlaneIps, IReadOnlyList<string> dnsServers)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(controlPlaneIps);
        if (names.DirectTable != "maldex_direct_" + names.Namespace[3..] || !Regex.IsMatch(names.DirectTable, "^maldex_direct_[0-9a-f]{12}$", RegexOptions.None, TimeSpan.FromSeconds(1)))
            throw new ArgumentException("Not a Direct table name.", nameof(names));
        var up = names.UplinkHost;
        if (!Regex.IsMatch(up, "^mxu[0-9a-f]{12}$", RegexOptions.None, TimeSpan.FromSeconds(1))) throw new ArgumentException("UplinkHost is not a valid mxu<12 hex> interface name.", nameof(names));
        if (defaultIface is null || !Iface().IsMatch(defaultIface) || defaultIface == "lo" || defaultIface.StartsWith("mx", StringComparison.Ordinal))
            throw new ArgumentException("The default-route interface name is not acceptable.", nameof(defaultIface));
        var cp = controlPlaneIps.Select(ip => NftRulesetBuilder.Ip(ip, "ControlPlaneIps")).Distinct().ToList();
        var dns = string.Join(", ", NftRulesetBuilder.ValidateDirectDns(dnsServers, cp));
        var blocked = string.Join(", ", NftRulesetBuilder.PrivateRanges.Concat(cp));

        var table = names.DirectTable;
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append('\n');
        L($"table inet {table} {{}}");
        L($"delete table inet {table}");
        L($"table inet {table} {{");
        L("\tchain input {");
        L("\t\ttype filter hook input priority -5; policy accept;");
        L($"\t\tiifname \"{up}\" drop");
        L("\t}");
        L("\tchain forward {");
        L("\t\ttype filter hook forward priority -5; policy accept;");
        L($"\t\tiifname \"{up}\" meta nfproto != ipv4 drop");
        L($"\t\tiifname \"{up}\" oifname \"{defaultIface}\" ip daddr {{ {dns} }} udp dport 53 accept");
        L($"\t\tiifname \"{up}\" oifname \"{defaultIface}\" ip daddr {{ {dns} }} tcp dport 53 accept");
        L($"\t\tiifname \"{up}\" ip daddr {{ {blocked} }} drop");
        L($"\t\tiifname \"{up}\" oifname \"{defaultIface}\" accept");
        L($"\t\tiifname \"{up}\" drop");
        L($"\t\toifname \"{up}\" iifname \"{defaultIface}\" ct state established,related accept");
        L($"\t\toifname \"{up}\" drop");
        L("\t}");
        L("\tchain postrouting {");
        L("\t\ttype nat hook postrouting priority srcnat; policy accept;");
        L($"\t\tiifname \"{up}\" oifname \"{defaultIface}\" masquerade");
        L("\t}");
        L("}");
        return sb.ToString();
    }
}

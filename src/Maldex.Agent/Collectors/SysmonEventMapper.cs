using System.Net;
using Maldex.Core.Agent;

namespace Maldex.Agent.Collectors;

/// <summary>
/// Maps Sysmon event fields to protocol messages. Pure and platform neutral. Output is shaped to pass
/// <see cref="AgentMessageValidator"/>; events that cannot be expressed in the protocol map to null.
/// </summary>
public static class SysmonEventMapper
{
    public static AgentMessage? Map(int eventId, DateTimeOffset at, IReadOnlyDictionary<string, string> f, int ignorePid = -1)
    {
        string S(string k) => f.TryGetValue(k, out var v) ? v : "";
        string? N(string k) => f.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        int I(string k) => int.TryParse(S(k), out var v) ? v : 0;

        if (ignorePid >= 0 && I("ProcessId") == ignorePid) return null;

        switch (eventId)
        {
            case 1:
                return new ProcessStartedMessage(at, I("ProcessId"), I("ParentProcessId"), S("Image"), S("CommandLine"), N("User"), N("IntegrityLevel"));
            case 5:
                return new ProcessExitedMessage(at, I("ProcessId"), 0);
            case 11:
                return new FileEventMessage(at, I("ProcessId"), "create", S("TargetFilename"));
            case 23:
                return new FileEventMessage(at, I("ProcessId"), "delete", S("TargetFilename"));
            case 12:
                var op = S("EventType") switch
                {
                    "CreateKey" => "create",
                    "DeleteKey" or "DeleteValue" => "delete",
                    _ => null,
                };
                return op is null ? null : new RegistryEventMessage(at, I("ProcessId"), op, S("TargetObject"));
            case 13:
                var target = S("TargetObject");
                var cut = target.LastIndexOf('\\');
                var key = cut > 0 ? target[..cut] : target;
                var valueName = cut > 0 ? target[(cut + 1)..] : null;
                return new RegistryEventMessage(at, I("ProcessId"), "set", key, valueName, N("Details"));
            case 14:
                return null; // RenameKey has no protocol equivalent
            case 3:
                var outbound = !string.Equals(S("Initiated"), "false", StringComparison.OrdinalIgnoreCase);
                var remoteAddr = S(outbound ? "DestinationIp" : "SourceIp");
                var remotePort = I(outbound ? "DestinationPort" : "SourcePort");
                var localAddr = N(outbound ? "SourceIp" : "DestinationIp");
                int? localPort = int.TryParse(S(outbound ? "SourcePort" : "DestinationPort"), out var lp) ? lp : null;
                if (remotePort is < 1 or > 65535) return null;
                return new NetworkEventMessage(at, I("ProcessId"), S("Protocol").ToLowerInvariant(), remoteAddr, remotePort, localAddr, localPort);
            case 22:
                return new DnsEventMessage(at, I("ProcessId"), S("QueryName"), null, ParseAnswers(N("QueryResults")));
            default:
                return null;
        }
    }

    /// <summary>Sysmon QueryResults looks like "::ffff:1.2.3.4;type:  5 www.x.com;2001:db8::1;". Keeps parseable IPs only.</summary>
    internal static string[]? ParseAnswers(string? results)
    {
        if (results is null) return null;
        var list = new List<string>();
        foreach (var raw in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var s = raw.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) ? raw[7..] : raw;
            if (IPAddress.TryParse(s, out var ip)) list.Add(ip.ToString());
        }
        return list.ToArray();
    }
}

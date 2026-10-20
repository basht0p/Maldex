using Maldex.Core.Agent;

namespace Maldex.Agent.Collectors;

/// <summary>Maps Sysmon event fields to protocol messages. Pure and platform neutral.</summary>
public static class SysmonEventMapper
{
    public static AgentMessage? Map(int eventId, DateTimeOffset at, IReadOnlyDictionary<string, string> f)
    {
        string S(string k) => f.TryGetValue(k, out var v) ? v : "";
        string? N(string k) => f.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        int I(string k) => int.TryParse(S(k), out var v) ? v : 0;

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
            case 13:
            case 14:
                var op = eventId switch
                {
                    12 => S("EventType") is { Length: > 0 } t ? t : "create_delete",
                    13 => "set_value",
                    _ => "rename",
                };
                return new RegistryEventMessage(at, I("ProcessId"), op, S("TargetObject"), N("NewName") ?? ValueName(S("TargetObject"), eventId), N("Details"));
            case 3:
                return new NetworkEventMessage(at, I("ProcessId"), S("Protocol"), S("DestinationIp"), I("DestinationPort"),
                    N("SourceIp"), int.TryParse(S("SourcePort"), out var lp) ? lp : null);
            case 22:
                var results = N("QueryResults");
                var answers = results?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return new DnsEventMessage(at, I("ProcessId"), S("QueryName"), N("QueryStatus"), answers);
            default:
                return null;
        }
    }

    private static string? ValueName(string targetObject, int eventId) =>
        eventId == 13 && targetObject.LastIndexOf('\\') is var i and >= 0 ? targetObject[(i + 1)..] : null;
}

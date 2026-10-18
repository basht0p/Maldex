using System.Buffers;

namespace Maldex.Core.Agent;

public static class AgentMessageValidator
{
    /// <summary>Returns null when valid, otherwise the reason.</summary>
    public static string? Validate(AgentMessage message) => message switch
    {
        ReadyMessage m => Req(m.AgentVersion, "AgentVersion") ?? Req(m.Hostname, "Hostname"),
        ProcessStartedMessage m => Pid(m.Pid) ?? (m.ParentPid < 0 ? "ParentPid is negative." : null)
            ?? Req(m.ImagePath, "ImagePath") ?? Req(m.CommandLine, "CommandLine"),
        ProcessExitedMessage m => Pid(m.Pid),
        FileEventMessage m => Pid(m.Pid) ?? OneOf(m.Operation, "Operation", "create", "write", "delete", "rename")
            ?? Req(m.Path, "Path"),
        RegistryEventMessage m => Pid(m.Pid) ?? OneOf(m.Operation, "Operation", "set", "create", "delete")
            ?? Req(m.Key, "Key"),
        NetworkEventMessage m => Pid(m.Pid) ?? OneOf(m.Protocol, "Protocol", "tcp", "udp")
            ?? Req(m.RemoteAddress, "RemoteAddress")
            ?? (m.RemotePort is < 1 or > 65535 ? "RemotePort out of range." : null)
            ?? (m.LocalPort is < 0 or > 65535 ? "LocalPort out of range." : null),
        DnsEventMessage m => Pid(m.Pid) ?? Req(m.Query, "Query")
            ?? (m.Answers is not null && Array.Exists(m.Answers, a => a is null) ? "Answers contains null." : null),
        ScreenshotMessage m => OneOf(m.Format, "Format", "jpeg", "png") ?? Base64(m.ImageBase64),
        CommandResultMessage m => Req(m.CommandId, "CommandId"),
        _ => "Unknown message type.",
    };

    private static string? Pid(int pid) => pid < 0 ? "Pid is negative." : null;

    private static string? Req(string? s, string name) => s is null ? $"{name} is missing." : null;

    private static string? OneOf(string? s, string name, params string[] allowed)
    {
        if (s is null) return $"{name} is missing.";
        foreach (var a in allowed)
            if (string.Equals(s, a, StringComparison.Ordinal)) return null;
        return $"{name} is not an allowed value.";
    }

    private static string? Base64(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "ImageBase64 is empty.";
        var buf = ArrayPool<byte>.Shared.Rent(s.Length / 4 * 3 + 3);
        try
        {
            return Convert.TryFromBase64String(s, buf, out var n) && n > 0 ? null : "ImageBase64 is not valid base64.";
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}

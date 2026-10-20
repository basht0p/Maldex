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
        FileChunkMessage m => ValidateFileChunk(m),
        SelfTestResultMessage m => ValidateSelfTest(m),
        _ => "Unknown message type.",
    };

    private const int MaxPathChars = 32_768;
    private const int MaxIdChars = 256;

    private const int MaxProbes = 64;
    private const int MaxProbeNameChars = 64;
    private const int MaxEchoChars = 256;
    private const int MaxGuestAddresses = 16;
    private const int MaxIpChars = 45;

    private static string? ValidateSelfTest(SelfTestResultMessage m)
    {
        if (string.IsNullOrEmpty(m.CommandId) || m.CommandId.Length > MaxIdChars) return "CommandId is missing or too long.";
        if (m.Probes is null) return "Probes is missing.";
        if (m.Probes.Count > MaxProbes) return "Too many probes.";
        foreach (var p in m.Probes)
        {
            if (p is null) return "Probes contains null.";
            if (string.IsNullOrEmpty(p.Name) || p.Name.Length > MaxProbeNameChars) return "Probe name is missing or too long.";
        }
        if (m.EchoBody is { Length: > MaxEchoChars }) return "EchoBody is too long.";
        if (m.GuestAddresses is not null)
        {
            if (m.GuestAddresses.Count > MaxGuestAddresses) return "Too many guest addresses.";
            foreach (var a in m.GuestAddresses)
                if (a is null || a.Length > MaxIpChars || !System.Net.IPAddress.TryParse(a, out _)) return "Guest address is not an IP address.";
        }
        return null;
    }

    private static string? ValidateFileChunk(FileChunkMessage m)
    {
        if (string.IsNullOrEmpty(m.TransferId) || m.TransferId.Length > MaxIdChars) return "TransferId is missing or too long.";
        if (m.Count is < 1 or > AgentLimits.MaxChunks) return "Count out of range.";
        if (m.Index < 0 || m.Index >= m.Count) return "Index out of range.";
        if (m.TotalBytes is < 0 or > AgentLimits.MaxDroppedFileBytes) return "TotalBytes out of range.";
        if (!IsLowerHex64(m.Sha256)) return "Sha256 is not 64 lowercase hex characters.";
        if (string.IsNullOrEmpty(m.Path) || m.Path.Length > MaxPathChars) return "Path is missing or too long.";
        return ChunkData(m.DataBase64, m.TotalBytes);
    }

    private static bool IsLowerHex64(string? s)
    {
        if (s is null || s.Length != 64) return false;
        foreach (var c in s)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    /// <summary>Data must be valid base64 that decodes to at most one chunk and at most the declared total. Empty is allowed (an empty file).</summary>
    private static string? ChunkData(string? s, long totalBytes)
    {
        if (s is null) return "DataBase64 is missing.";
        if (s.Length == 0) return null;
        // Reject by length before allocating: one chunk is at most 4/3 of 512 KiB characters.
        if (s.Length > (AgentLimits.ChunkBytes + 2) / 3 * 4) return "Chunk data exceeds the chunk size limit.";
        var buf = ArrayPool<byte>.Shared.Rent(s.Length / 4 * 3 + 3);
        try
        {
            if (!Convert.TryFromBase64String(s, buf, out var n)) return "DataBase64 is not valid base64.";
            if (n > AgentLimits.ChunkBytes) return "Chunk data exceeds the chunk size limit.";
            return n > totalBytes ? "Chunk data exceeds the declared TotalBytes." : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

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

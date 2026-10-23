using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maldex.Core.Agent;

/// <summary>Shared serializer options for the agent protocol (reader, writer, replay files).</summary>
public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            AllowOutOfOrderMetadataProperties = true,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }
}

/// <summary>Telemetry from the in-guest agent. UNTRUSTED: validate before use.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ReadyMessage), "ready")]
[JsonDerivedType(typeof(ProcessStartedMessage), "process_started")]
[JsonDerivedType(typeof(ProcessExitedMessage), "process_exited")]
[JsonDerivedType(typeof(FileEventMessage), "file")]
[JsonDerivedType(typeof(RegistryEventMessage), "registry")]
[JsonDerivedType(typeof(NetworkEventMessage), "network")]
[JsonDerivedType(typeof(DnsEventMessage), "dns")]
[JsonDerivedType(typeof(ScreenshotMessage), "screenshot")]
[JsonDerivedType(typeof(CommandResultMessage), "command_result")]
[JsonDerivedType(typeof(FileChunkMessage), "file_chunk")]
[JsonDerivedType(typeof(SelfTestResultMessage), "self_test_result")]
public abstract record AgentMessage;

/// <param name="Pid">The agent's own process id (additive; null from older agents). Processes it launches (the opened sample)
/// have it as their parent, which is how the host scopes dropped-file collection to the sample's process tree.</param>
public sealed record ReadyMessage(string AgentVersion, string Hostname, int? Pid = null) : AgentMessage;

public sealed record ProcessStartedMessage(DateTimeOffset At, int Pid, int ParentPid, string ImagePath, string CommandLine, string? User = null, string? Integrity = null) : AgentMessage;

public sealed record ProcessExitedMessage(DateTimeOffset At, int Pid, int ExitCode) : AgentMessage;

public sealed record FileEventMessage(DateTimeOffset At, int Pid, string Operation, string Path, string? NewPath = null) : AgentMessage;

public sealed record RegistryEventMessage(DateTimeOffset At, int Pid, string Operation, string Key, string? ValueName = null, string? Data = null) : AgentMessage;

public sealed record NetworkEventMessage(DateTimeOffset At, int Pid, string Protocol, string RemoteAddress, int RemotePort, string? LocalAddress = null, int? LocalPort = null) : AgentMessage;

public sealed record DnsEventMessage(DateTimeOffset At, int Pid, string Query, string? QueryType = null, string[]? Answers = null) : AgentMessage;

public sealed record ScreenshotMessage(DateTimeOffset At, string Format, string ImageBase64) : AgentMessage;

public sealed record CommandResultMessage(string CommandId, bool Success, string? Error = null) : AgentMessage;

/// <summary>
/// One chunk of a file the guest is sending (a collected dropped file). <c>TransferId</c> is the <c>CollectFileCommand.CommandId</c>.
/// UNTRUSTED. At most <see cref="AgentLimits.ChunkBytes"/> raw bytes of data per chunk.
/// </summary>
public sealed record FileChunkMessage(
    DateTimeOffset At,
    string TransferId,
    int Index,
    int Count,
    long TotalBytes,
    string Sha256,
    string Path,
    [property: JsonConverter(typeof(RawBase64Converter))] string DataBase64) : AgentMessage;

/// <summary>Host-to-guest command.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(OpenSampleCommand), "open_sample")]
[JsonDerivedType(typeof(OpenUrlCommand), "open_url")]
[JsonDerivedType(typeof(ShutdownCommand), "shutdown")]
[JsonDerivedType(typeof(PutFileChunkCommand), "put_file_chunk")]
[JsonDerivedType(typeof(CollectFileCommand), "collect_file")]
[JsonDerivedType(typeof(SelfTestCommand), "self_test")]
public abstract record AgentCommand(string CommandId);

public sealed record OpenSampleCommand(string CommandId, string FileName, string BlobKey) : AgentCommand(CommandId);

public sealed record OpenUrlCommand(string CommandId, string Url) : AgentCommand(CommandId);

public sealed record ShutdownCommand(string CommandId) : AgentCommand(CommandId);

/// <summary>One chunk of the sample being delivered. <c>TransferId</c> is the sample's blob key; chunks arrive in order, at most <see cref="AgentLimits.ChunkBytes"/> raw bytes each.</summary>
public sealed record PutFileChunkCommand(
    string CommandId,
    string TransferId,
    int Index,
    int Count,
    long TotalBytes,
    string Sha256,
    [property: JsonConverter(typeof(RawBase64Converter))] string DataBase64) : AgentCommand(CommandId);

/// <summary>Asks the guest to send back the file at <c>Path</c> as <see cref="FileChunkMessage"/>s followed by a command result.</summary>
public sealed record CollectFileCommand(string CommandId, string Path) : AgentCommand(CommandId);

/// <summary>One thing the isolation self-test tries to reach: every port of <c>Ports</c> on <c>Host</c>.</summary>
public sealed record ProbeTarget(string Name, string Host, IReadOnlyList<int> Ports);

/// <summary>Asks the guest to try to reach each target (TCP connect) and to fetch <c>EchoUrl</c>, then answer with a <see cref="SelfTestResultMessage"/>.</summary>
public sealed record SelfTestCommand(string CommandId, IReadOnlyList<ProbeTarget> Targets, string EchoUrl) : AgentCommand(CommandId);

/// <summary><c>Reachable</c> = at least one port of the target accepted a connection.</summary>
public sealed record ProbeResult(string Name, bool Reachable);

/// <summary>
/// The guest's answer to a <see cref="SelfTestCommand"/> (<c>CommandId</c> is the command's). UNTRUSTED. <c>EchoBody</c> is the
/// first 256 characters of the echo response, null if it failed. <c>GuestAddresses</c> (additive; null from older agents) are
/// the guest's IP addresses on the interface(s) with a default gateway, or every non-loopback address if none got one in time.
/// </summary>
public sealed record SelfTestResultMessage(DateTimeOffset At, string CommandId, IReadOnlyList<ProbeResult> Probes, string? EchoBody,
    IReadOnlyList<string>? GuestAddresses = null) : AgentMessage;

/// <summary>Limits shared by both ends of a bulk transfer.</summary>
public static class AgentLimits
{
    /// <summary>Raw bytes per chunk.</summary>
    public const int ChunkBytes = 512 * 1024;

    /// <summary>Most chunks in one transfer.</summary>
    public const int MaxChunks = 64;

    /// <summary>Largest dropped file the host accepts.</summary>
    public const long MaxDroppedFileBytes = 16L * 1024 * 1024;

    /// <summary>Largest sample the guest buffers.</summary>
    public const long MaxInboundBytes = 32L * 1024 * 1024;
}

/// <summary>
/// Writes a base64 string verbatim. The default encoder escapes every '+' as a 6-byte unicode escape, so a full 512 KiB
/// chunk of worst-case bytes (all '+', about 700 KB of base64) would serialise to about 4.2 MB, far past the 1 MiB frame limit.
/// Base64 text is only A-Z a-z 0-9 + / =, so writing it unescaped is safe; any other content is written escaped as usual.
/// </summary>
public sealed class RawBase64Converter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        foreach (var c in value)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '='))
            {
                writer.WriteStringValue(value);
                return;
            }
        }
        writer.WriteRawValue(string.Concat("\"", value, "\""), skipInputValidation: true);
    }
}

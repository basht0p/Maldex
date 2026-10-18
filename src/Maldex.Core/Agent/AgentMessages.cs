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
public abstract record AgentMessage;

public sealed record ReadyMessage(string AgentVersion, string Hostname) : AgentMessage;

public sealed record ProcessStartedMessage(DateTimeOffset At, int Pid, int ParentPid, string ImagePath, string CommandLine, string? User = null, string? Integrity = null) : AgentMessage;

public sealed record ProcessExitedMessage(DateTimeOffset At, int Pid, int ExitCode) : AgentMessage;

public sealed record FileEventMessage(DateTimeOffset At, int Pid, string Operation, string Path, string? NewPath = null) : AgentMessage;

public sealed record RegistryEventMessage(DateTimeOffset At, int Pid, string Operation, string Key, string? ValueName = null, string? Data = null) : AgentMessage;

public sealed record NetworkEventMessage(DateTimeOffset At, int Pid, string Protocol, string RemoteAddress, int RemotePort, string? LocalAddress = null, int? LocalPort = null) : AgentMessage;

public sealed record DnsEventMessage(DateTimeOffset At, int Pid, string Query, string? QueryType = null, string[]? Answers = null) : AgentMessage;

public sealed record ScreenshotMessage(DateTimeOffset At, string Format, string ImageBase64) : AgentMessage;

public sealed record CommandResultMessage(string CommandId, bool Success, string? Error = null) : AgentMessage;

/// <summary>Host-to-guest command.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(OpenSampleCommand), "open_sample")]
[JsonDerivedType(typeof(OpenUrlCommand), "open_url")]
[JsonDerivedType(typeof(ShutdownCommand), "shutdown")]
public abstract record AgentCommand(string CommandId);

public sealed record OpenSampleCommand(string CommandId, string FileName, string BlobKey) : AgentCommand(CommandId);

public sealed record OpenUrlCommand(string CommandId, string Url) : AgentCommand(CommandId);

public sealed record ShutdownCommand(string CommandId) : AgentCommand(CommandId);

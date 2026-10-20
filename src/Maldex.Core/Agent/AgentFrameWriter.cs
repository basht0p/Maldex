namespace Maldex.Core.Agent;

public static class AgentFrameWriter
{
    /// <summary>Host to guest.</summary>
    public static ValueTask WriteAsync(Stream stream, AgentCommand command, CancellationToken ct) =>
        FrameCodec.WriteAsync<AgentCommand>(stream, command, "Command", ct);

    /// <summary>Guest to host.</summary>
    public static ValueTask WriteAsync(Stream stream, AgentMessage message, CancellationToken ct) =>
        FrameCodec.WriteAsync<AgentMessage>(stream, message, "Message", ct);
}

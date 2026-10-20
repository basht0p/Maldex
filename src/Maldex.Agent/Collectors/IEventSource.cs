using Maldex.Core.Agent;

namespace Maldex.Agent.Collectors;

/// <summary>A producer of telemetry messages; the host forwards each one as a frame.</summary>
public interface IEventSource
{
    IAsyncEnumerable<AgentMessage> Events(CancellationToken ct);
}

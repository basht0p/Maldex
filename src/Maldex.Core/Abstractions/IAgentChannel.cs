using Maldex.Core.Agent;

namespace Maldex.Core.Abstractions;

public interface IAgentChannel : IAsyncDisposable
{
    /// <summary>Yields only validated messages; completes when the channel closes.</summary>
    IAsyncEnumerable<AgentMessage> ReadAsync(CancellationToken ct);

    int Violations { get; }

    Task SendAsync(AgentCommand command, CancellationToken ct);
}

public interface IAgentChannelFactory
{
    Task<IAgentChannel> ConnectAsync(VmHandle vm, CancellationToken ct);
}

using System.Runtime.CompilerServices;
using Maldex.Core.Agent;

namespace Maldex.Agent.Collectors;

/// <summary>Yields nothing (and completes immediately).</summary>
public sealed class NullEventSource : IEventSource
{
    public async IAsyncEnumerable<AgentMessage> Events([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
}

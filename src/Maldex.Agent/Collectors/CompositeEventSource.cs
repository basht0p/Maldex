using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Maldex.Core.Agent;

namespace Maldex.Agent.Collectors;

/// <summary>Merges several sources into one stream. A failing source is dropped; the others keep flowing.</summary>
public sealed class CompositeEventSource(IReadOnlyList<IEventSource> sources) : IEventSource
{
    public async IAsyncEnumerable<AgentMessage> Events([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentMessage>();
        var pumps = sources.Select(s => Task.Run(async () =>
        {
            try
            {
                await foreach (var m in s.Events(ct).ConfigureAwait(false))
                    await channel.Writer.WriteAsync(m, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Event source {s.GetType().Name} stopped: {ex.Message}");
            }
        }, CancellationToken.None)).ToArray();

        _ = Task.WhenAll(pumps).ContinueWith(_ => channel.Writer.TryComplete(), TaskScheduler.Default);

        await foreach (var m in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return m;
    }
}

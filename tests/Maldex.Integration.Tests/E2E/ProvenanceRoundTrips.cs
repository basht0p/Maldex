using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;

namespace Maldex.Integration.Tests.E2E;

/// <summary>
/// Test-side observation of the provenance round trips a real host makes (the product logs neither the attempts nor the
/// time): wraps the host's agent channel factory and, per connection that sends a <see cref="ProvenanceCommand"/>, records how
/// many were sent and how long the first answer took from the first send. Changes nothing on the wire.
/// </summary>
internal sealed class ProvenanceRoundTrips
{
    internal sealed class Connection(string vm, int number)
    {
        public string Vm { get; } = vm;
        public int Number { get; } = number;
        public int Sent;
        public long FirstSentAt;
        public TimeSpan? AnsweredAfter;
    }

    private readonly ConcurrentQueue<Connection> _connections = new();
    private readonly ConcurrentDictionary<string, int> _perVm = new();

    public IReadOnlyList<Connection> Connections => _connections.Where(c => c.Sent > 0).ToList();

    /// <summary>One line per connection that asked for provenance; <paramref name="label"/> names a VM (job vs verification boot).</summary>
    public IEnumerable<string> Describe(Func<string, string> label) =>
        Connections.Select(c => $"{label(c.Vm)} connection #{c.Number}: {c.Sent} provenance request(s), "
            + (c.AnsweredAfter is { } t ? $"answered {t.TotalSeconds:0.0} s after the first" : "no answer"));

    public IAgentChannelFactory Wrap(IAgentChannelFactory inner) => new Factory(inner, this);

    private Connection Open(string vm) => Enqueue(new Connection(vm, _perVm.AddOrUpdate(vm, 1, (_, n) => n + 1)));

    private Connection Enqueue(Connection c)
    {
        _connections.Enqueue(c);
        return c;
    }

    private sealed class Factory(IAgentChannelFactory inner, ProvenanceRoundTrips log) : IAgentChannelFactory
    {
        public async Task<IAgentChannel> ConnectAsync(VmHandle vm, CancellationToken ct) =>
            new Channel(await inner.ConnectAsync(vm, ct).ConfigureAwait(false), log.Open(vm.Name));
    }

    private sealed class Channel(IAgentChannel inner, Connection c) : IAgentChannel
    {
        public int Violations => inner.Violations;

        public async IAsyncEnumerable<AgentMessage> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var m in inner.ReadAsync(ct).ConfigureAwait(false))
            {
                if (m is ProvenanceMessage && c.AnsweredAfter is null && c.Sent > 0)
                    c.AnsweredAfter = Stopwatch.GetElapsedTime(c.FirstSentAt);
                yield return m;
            }
        }

        public Task SendAsync(AgentCommand command, CancellationToken ct)
        {
            if (command is ProvenanceCommand && Interlocked.Increment(ref c.Sent) == 1) c.FirstSentAt = Stopwatch.GetTimestamp();
            return inner.SendAsync(command, ct);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

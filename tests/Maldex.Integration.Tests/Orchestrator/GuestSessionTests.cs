using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Orchestrator.Templates;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

public class GuestSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A guest the test scripts: it answers each provenance request, and the test can push any frame.</summary>
    private sealed class ScriptedChannel : IAgentChannel
    {
        private readonly Channel<AgentMessage> _incoming = Channel.CreateUnbounded<AgentMessage>();
        public List<ProvenanceCommand> Requests { get; } = [];
        public int Violations => 0;

        public void Push(AgentMessage message) => _incoming.Writer.TryWrite(message);

        public Task SendAsync(AgentCommand command, CancellationToken ct)
        {
            if (command is ProvenanceCommand p)
            {
                lock (Requests) Requests.Add(p);
                Push(new ProvenanceMessage(p.CommandId, "Windows 11 Pro", "26300.1", "1.0.0+aaaaaaaaaaaa", false, false));
            }
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<AgentMessage> ReadAsync(CancellationToken ct) => _incoming.Reader.ReadAllAsync(ct);

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ALateResultForAFinishedProvenanceRequest_NeverReachesTheNextTransfer()
    {
        await using var channel = new ScriptedChannel();
        await using var session = new GuestSession(channel);
        await session.AskProvenanceWithRetryAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeProvider.System, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var retired = Assert.Single(channel.Requests).CommandId;

        // The next step (a component transfer, say) opens the inbox; then a late failure for the finished request arrives, and
        // after it the reply that step is waiting for.
        using var lease = session.Inbox.Open();
        channel.Push(new CommandResultMessage(retired, false, "late"));
        channel.Push(new CommandResultMessage("sentinel", true));

        var first = await lease.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("sentinel", Assert.IsType<CommandResultMessage>(first).CommandId);
    }
}

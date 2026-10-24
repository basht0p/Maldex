using System.Collections.Concurrent;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Text;

namespace Maldex.Orchestrator.Templates;

/// <summary>
/// A template job's view of one guest: reads the agent channel in the background, remembers the ready message, routes
/// provenance answers, and hands command results and transfer replies to a <see cref="TransferInbox"/> (so <see cref="SampleDelivery"/> can be reused).
/// The job owns the channel; this only reads it, and stops reading when disposed.
/// </summary>
internal sealed class GuestSession : IAsyncDisposable
{
    private readonly IAgentChannel _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly TaskCompletionSource<ReadyMessage> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProvenanceMessage>> _provenance = new();

    public GuestSession(IAgentChannel channel)
    {
        _channel = channel;
        _pump = Task.Run(PumpAsync);
    }

    public TransferInbox Inbox { get; } = new();

    /// <summary>Completes when the agent's ready message arrives; faults if the channel ends first.</summary>
    public Task<ReadyMessage> Ready => _ready.Task;

    /// <summary>Asks for the provenance report; the task completes with the answer, or faults when the agent refuses or disconnects.</summary>
    public async Task<ProvenanceMessage> AskProvenanceAsync(CancellationToken ct)
    {
        var command = new ProvenanceCommand(Guid.NewGuid().ToString("N"));
        var answer = new TaskCompletionSource<ProvenanceMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _provenance[command.CommandId] = answer;
        try
        {
            await _channel.SendAsync(command, ct).ConfigureAwait(false);
            return await answer.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _provenance.TryRemove(command.CommandId, out _);
        }
    }

    private async Task PumpAsync()
    {
        Exception? error = null;
        try
        {
            await foreach (var message in _channel.ReadAsync(_cts.Token).ConfigureAwait(false))
            {
                switch (message)
                {
                    case ReadyMessage r:
                        _ready.TrySetResult(r);
                        break;
                    case ProvenanceMessage p when _provenance.TryGetValue(p.CommandId, out var waiting):
                        waiting.TrySetResult(p);
                        break;
                    case CommandResultMessage { Success: false } f when _provenance.TryGetValue(f.CommandId, out var refused):
                        refused.TrySetException(new InvalidOperationException($"the agent refused the provenance request ({GuestText.Clean(f.Error, 200)})"));
                        break;
                    default:
                        await Inbox.OfferAsync(message, _cts.Token).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        finally
        {
            Inbox.Complete(error);
            var gone = new InvalidOperationException("the agent disconnected");
            _ready.TrySetException(gone);
            foreach (var waiting in _provenance.Values) waiting.TrySetException(gone);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        _cts.Dispose();
    }
}

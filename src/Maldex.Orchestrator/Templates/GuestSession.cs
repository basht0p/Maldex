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
    /// <summary>Ids of finished provenance requests: late frames for them are dropped, never passed on to the next step.</summary>
    private readonly ConcurrentDictionary<string, byte> _retired = new();
    private TaskCompletionSource _readyTick = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Set once the pump has ended: a request registered after it can never be answered and fails at once.</summary>
    private volatile bool _ended;

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
        Register(command.CommandId, answer);
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

    /// <summary>
    /// Asks for the provenance report until any answer arrives: sends a request with a fresh id, then again every
    /// <paramref name="retryInterval"/> and at once whenever a <c>ready</c> frame arrives (the agent has just reconnected). After a host
    /// reconnect the agent needs 1-10 s to come back, and a request sent in that gap is lost, so one send is not enough. The first
    /// reply to any sent id wins; replies to the others are dropped here, also after this returns.
    /// </summary>
    /// <exception cref="TimeoutException">No answer within <paramref name="timeout"/> (<paramref name="time"/> measures it).</exception>
    /// <exception cref="InvalidOperationException">The agent refused the request, or went away.</exception>
    public async Task<ProvenanceMessage> AskProvenanceWithRetryAsync(TimeSpan retryInterval, TimeSpan timeout, TimeProvider time, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<ProvenanceMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<string>();
        using var deadline = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            while (true)
            {
                var tick = Volatile.Read(ref _readyTick).Task; // taken before the send: a ready during it still resends
                var command = new ProvenanceCommand(Guid.NewGuid().ToString("N"));
                sent.Add(command.CommandId);
                Register(command.CommandId, answer);
                if (answer.Task.IsCompleted) return await answer.Task.ConfigureAwait(false); // the agent is gone
                await _channel.SendAsync(command, linked.Token).ConfigureAwait(false);

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                var pause = Task.Delay(retryInterval, time, wait.Token);
                var first = await Task.WhenAny(answer.Task, tick, pause).ConfigureAwait(false);
                await wait.CancelAsync().ConfigureAwait(false);
                if (first == answer.Task) return await answer.Task.ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
            }
        }
        catch (Exception) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no provenance answer within {timeout.TotalSeconds:0} s");
        }
        finally
        {
            foreach (var id in sent)
            {
                _retired[id] = 0;
                _provenance.TryRemove(id, out _);
            }
        }
    }

    /// <summary>
    /// Routes answers for <paramref name="commandId"/> to <paramref name="answer"/>. The pump fails every registered request when it
    /// ends; one registered after that is failed here (checked after registering, so one of the two always sees the other).
    /// </summary>
    private void Register(string commandId, TaskCompletionSource<ProvenanceMessage> answer)
    {
        _provenance[commandId] = answer;
        if (_ended) answer.TrySetException(Gone());
    }

    private static InvalidOperationException Gone() => new("the agent disconnected");

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
                        Interlocked.Exchange(ref _readyTick, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                        break;
                    case ProvenanceMessage p when _retired.ContainsKey(p.CommandId):
                    case CommandResultMessage r when _retired.ContainsKey(r.CommandId):
                        break; // a late frame of a finished request
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
            _ended = true;
            var gone = Gone();
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

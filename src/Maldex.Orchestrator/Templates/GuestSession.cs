using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
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
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly TaskCompletionSource<ReadyMessage> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProvenanceMessage>> _provenance = new();
    /// <summary>Ids of finished provenance requests: late frames for them are dropped, never passed on to the next step.</summary>
    private readonly ConcurrentDictionary<string, byte> _retired = new();
    private TaskCompletionSource _readyTick = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Set once the pump has ended: a request registered after it can never be answered and fails at once.</summary>
    private volatile bool _ended;

    /// <param name="time">Measures <see cref="CollectFileAsync"/>'s timeout (default: the system clock).</param>
    public GuestSession(IAgentChannel channel, TimeProvider? time = null)
    {
        _channel = channel;
        _time = time ?? TimeProvider.System;
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
    /// Reads Defender's real-time protection until it has settled (right after a boot it briefly reads off before it turns on):
    /// a provenance reading (<see cref="AskProvenanceWithRetryAsync"/>, each bounded by <paramref name="timeout"/>) every
    /// <see cref="TemplateJobOptions.DefenderSettleInterval"/> over <see cref="TemplateJobOptions.DefenderSettleWindow"/>. The state
    /// is on if any reading says on (the readings stop there), else off if any says off, else null (unreadable). A failed reading
    /// is skipped while others succeed.
    /// </summary>
    /// <returns>The first successful reading (for the other checks) and the settled state.</returns>
    /// <exception cref="TimeoutException">No reading succeeded, the last one timed out.</exception>
    /// <exception cref="InvalidOperationException">No reading succeeded, the last one was refused or the agent went away.</exception>
    public async Task<SettledProvenance> AskSettledProvenanceAsync(TemplateJobOptions o, TimeSpan timeout, TimeProvider time, CancellationToken ct)
    {
        var start = time.GetUtcNow();
        ProvenanceMessage? first = null;
        Exception? lastFailure = null;
        var sawOff = false;
        while (true)
        {
            try
            {
                var p = await AskProvenanceWithRetryAsync(o.ProvenanceRetryInterval, timeout, time, ct).ConfigureAwait(false);
                first ??= p;
                if (p.RealTimeProtectionEnabled == true) return new SettledProvenance(first, true); // no later reading can change it
                sawOff |= p.RealTimeProtectionEnabled == false;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex; // skipped while another reading succeeds
            }

            if (time.GetUtcNow() - start + o.DefenderSettleInterval > o.DefenderSettleWindow) break;
            await Task.Delay(o.DefenderSettleInterval, time, ct).ConfigureAwait(false);
        }

        if (first is null) ExceptionDispatchInfo.Throw(lastFailure!);
        return new SettledProvenance(first, sawOff ? false : null);
    }

    /// <summary>
    /// Fetches one file from the guest with <c>collect_file</c> through the <see cref="Inbox"/> (which must not be open elsewhere),
    /// reassembled and hash-checked as dropped files are.
    /// </summary>
    /// <returns>The file's bytes, or null when the agent answers that it could not serve it (the file is missing).</returns>
    /// <exception cref="InvalidOperationException">The file is larger than <paramref name="maxBytes"/>, or its chunks do not reassemble.</exception>
    /// <exception cref="TimeoutException">No complete answer within <paramref name="timeout"/>.</exception>
    /// <exception cref="ChannelClosedException">The agent disconnected.</exception>
    public async Task<byte[]?> CollectFileAsync(string guestPath, long maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        var transfer = new FileTransfer(guestPath, maxBytes);
        using var deadline = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        using var lease = Inbox.Open();
        CommandResultMessage result;
        try
        {
            result = await transfer.ReceiveAsync(_channel, lease.Reader, linked.Token).ConfigureAwait(false);
        }
        catch (Exception) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no answer to collect_file within {timeout.TotalSeconds:0} s");
        }
        if (!result.Success) return null;
        var problem = transfer.Verify(out var content);
        if (problem is not null) throw new InvalidOperationException($"the file {GuestText.Clean(guestPath, 256)} was rejected: {problem}");
        return content;
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

/// <summary>What <see cref="GuestSession.AskSettledProvenanceAsync"/> read: its first successful reading and the settled real-time protection state.</summary>
internal sealed record SettledProvenance(ProvenanceMessage First, bool? RealTimeProtectionEnabled);

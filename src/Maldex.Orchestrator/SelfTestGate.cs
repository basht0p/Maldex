using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Core.Text;
using Maldex.Infrastructure.Isolation;

namespace Maldex.Orchestrator;

/// <summary>The pre-detonation isolation self-test failed. The message is safe to store as the task's failure reason.</summary>
public sealed class IsolationException(string message) : Exception(message);

/// <summary>The analyst asked to stop while the isolation self-test was running.</summary>
public sealed class SelfTestStoppedException() : Exception("stopped by analyst during the isolation self-test");

/// <summary>
/// The pre-detonation isolation gate. Asks the guest to probe the network, waits for its answer and returns the
/// <see cref="IsolationVerdict"/>. Every way of not getting a clean positive answer (timeout, refusal, disconnect, a bad
/// or missing result) is a failed verdict: the gate never passes by default.
/// </summary>
public sealed class SelfTestGate(TimeProvider time, TimeSpan? timeout = null, TimeSpan? pollInterval = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private const int MaxViolations = 10;

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(1);

    /// <param name="inbox">Where the run's single read loop routes the self-test answer. Opened here, so nothing else may hold it; other telemetry keeps flowing to ingestion.</param>
    /// <param name="stopRequested">Polled about once per poll interval; true ends the wait with <see cref="SelfTestStoppedException"/>.</param>
    public async Task<IsolationVerdict> RunAsync(IAgentChannel ch, TransferInbox inbox, NetworkMode mode, string? expectedEgressIp, IsolationProbePlan plan,
        CancellationToken ct, Func<CancellationToken, Task<bool>>? stopRequested = null)
    {
        var command = new SelfTestCommand(Guid.NewGuid().ToString("N"), plan.Targets, plan.EchoUrl);
        using var lease = inbox.Open();
        using var deadline = new CancellationTokenSource(_timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            if (stopRequested is not null && await stopRequested(linked.Token).ConfigureAwait(false)) throw new SelfTestStoppedException();
            await ch.SendAsync(command, linked.Token).ConfigureAwait(false);
            while (true)
            {
                var read = lease.Reader.ReadAsync(linked.Token).AsTask();
                while (!read.IsCompleted)
                {
                    using var tick = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    var wait = Task.Delay(_poll, time, tick.Token);
                    await Task.WhenAny(read, wait).ConfigureAwait(false);
                    await tick.CancelAsync().ConfigureAwait(false);
                    if (!read.IsCompleted && stopRequested is not null && await stopRequested(linked.Token).ConfigureAwait(false))
                    {
                        await linked.CancelAsync().ConfigureAwait(false);
                        try { await read.ConfigureAwait(false); } catch (Exception) { /* abandoned */ }
                        throw new SelfTestStoppedException();
                    }
                }

                switch (await read.ConfigureAwait(false))
                {
                    case SelfTestResultMessage r when r.CommandId == command.CommandId:
                        return IsolationVerifier.Verify(mode, plan.ExpectedNames, expectedEgressIp, r, plan.GuestIp);
                    case CommandResultMessage { Success: false } refused when refused.CommandId == command.CommandId:
                        return IsolationVerdict.Fail($"the agent refused the self-test ({GuestText.Clean(refused.Error, 200)})");
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return IsolationVerdict.Fail($"no self-test result within {_timeout.TotalSeconds:0} s");
        }
        catch (ChannelClosedException)
        {
            return IsolationVerdict.Fail(ch.Violations >= MaxViolations ? "agent protocol violations" : "the agent disconnected during the self-test");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return IsolationVerdict.Fail($"the self-test could not run: {GuestText.Clean(ex.Message, 200)}");
        }
    }
}

using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Infrastructure.Fakes;
using Maldex.Orchestrator.Templates;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

/// <summary>
/// A channel over a replay guest that behaves like the link right after a host reconnect: the first requests are lost
/// (the agent has not reconnected yet), and the test can inject frames the dropped session left behind.
/// </summary>
internal sealed class ReconnectGapChannel : IAgentChannel
{
    private readonly ReplayAgentChannel _inner;
    private readonly Channel<AgentMessage> _incoming = Channel.CreateUnbounded<AgentMessage>();
    private readonly Task _copy;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private int _dropsLeft;
    private readonly TaskCompletionSource _firstDropped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ReconnectGapChannel(ReplayAgentChannel inner, int dropProvenanceRequests)
    {
        _inner = inner;
        _dropsLeft = dropProvenanceRequests;
        _copy = Task.Run(async () =>
        {
            try { await foreach (var m in inner.ReadAsync(_stop.Token)) await _incoming.Writer.WriteAsync(m); }
            catch (OperationCanceledException) { }
            finally { _incoming.Writer.TryComplete(); }
        });
    }

    public int Violations => _inner.Violations;
    public List<ProvenanceCommand> ProvenanceRequests { get; } = [];

    /// <summary>Completes once the first request has been lost.</summary>
    public Task FirstDropped => _firstDropped.Task;

    public void Inject(AgentMessage message) => _incoming.Writer.TryWrite(message);

    public Task SendAsync(AgentCommand command, CancellationToken ct)
    {
        if (command is ProvenanceCommand p)
        {
            bool drop;
            lock (_gate)
            {
                ProvenanceRequests.Add(p);
                drop = _dropsLeft > 0;
                if (drop) _dropsLeft--;
            }
            if (drop)
            {
                _firstDropped.TrySetResult();
                return Task.CompletedTask;
            }
        }
        return _inner.SendAsync(command, ct);
    }

    public async IAsyncEnumerable<AgentMessage> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var m in _incoming.Reader.ReadAllAsync(ct)) yield return m;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _copy;
        await _inner.DisposeAsync();
    }
}

public class ProvenanceRetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(TemplateJobKit Kit, TemplateSealer Sealer, FakeTimeProvider Time, ReconnectGapChannel Channel, VmHandle Vm)> SetUpAsync(int drops, TimeSpan retry)
    {
        var kit = new TemplateJobKit();
        kit.Options.ProvenanceRetryInterval = retry;
        kit.Options.ProvenanceTimeout = TimeSpan.FromSeconds(60);
        var time = new FakeTimeProvider();
        var (replay, vm) = await kit.StartTemplateVmAsync(ready: false); // a second connection to a running agent sees no ready
        var channel = new ReconnectGapChannel(replay, drops);
        var sealer = new TemplateSealer(kit.Hypervisor, kit.Store, Microsoft.Extensions.Options.Options.Create(kit.Options), time);
        return (kit, sealer, time, channel, vm);
    }

    /// <summary>Advances the fake clock in small steps until <paramref name="run"/> ends (the waits under test are all fake-clock waits).</summary>
    private static async Task PumpAsync(FakeTimeProvider time, Task run)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!run.IsCompleted)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "The seal did not finish within 30 s of real time.");
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1, Ct); // the fake clock moves only a little per real millisecond, as the job harness does
        }
        await run;
    }

    [Fact]
    public async Task RequestsLostInTheReconnectGap_AreResent_UntilTheAgentAnswers()
    {
        var (kit, sealer, time, channel, vm) = await SetUpAsync(drops: 2, retry: TimeSpan.FromSeconds(3));
        await using var _ = kit;

        await PumpAsync(time, sealer.SealAsync(channel, vm, "win11-edit", kit.Bundle, Ct));

        Assert.Equal([("win11-edit", 0)], kit.Store.Seals);
        Assert.Equal(3, channel.ProvenanceRequests.Count); // two lost, the third answered
        Assert.Equal(3, channel.ProvenanceRequests.Select(r => r.CommandId).Distinct().Count()); // a fresh id each time
    }

    [Fact]
    public async Task AgentThatNeverAnswers_StillFailsAtTheTimeout_AfterResending()
    {
        var (kit, sealer, time, channel, vm) = await SetUpAsync(drops: int.MaxValue, retry: TimeSpan.FromSeconds(3));
        await using var _ = kit;

        var ex = await Assert.ThrowsAsync<TemplateSealException>(() => PumpAsync(time, sealer.SealAsync(channel, vm, "win11-edit", kit.Bundle, Ct)));

        Assert.StartsWith("the guest agent did not answer", ex.Message);
        Assert.InRange(channel.ProvenanceRequests.Count, 15, 25); // about one every 3 s for 60 s
        Assert.Empty(kit.Store.Seals);
    }

    [Fact]
    public async Task AReadyFrame_TriggersAnImmediateResend()
    {
        // The retry interval is longer than the whole timeout, so only the ready frame can rescue the lost request.
        var (kit, sealer, time, channel, vm) = await SetUpAsync(drops: 1, retry: TimeSpan.FromHours(1));
        await using var _ = kit;

        var run = sealer.SealAsync(channel, vm, "win11-edit", kit.Bundle, Ct);
        await channel.FirstDropped.WaitAsync(Ct);
        Assert.Single(channel.ProvenanceRequests);
        channel.Inject(new ReadyMessage("0.1.0-fake+000000000000", "h")); // the agent reconnected and says hello

        await PumpAsync(time, run);

        Assert.Equal(2, channel.ProvenanceRequests.Count);
        Assert.Equal([("win11-edit", 0)], kit.Store.Seals);
    }

    [Fact]
    public async Task LateRepliesToOldRequests_AfterSuccess_AreIgnored()
    {
        var (kit, sealer, time, channel, vm) = await SetUpAsync(drops: 1, retry: TimeSpan.FromSeconds(3));
        await using var _ = kit;

        var run = sealer.SealAsync(channel, vm, "win11-edit", kit.Bundle, Ct);
        await channel.FirstDropped.WaitAsync(Ct);
        var old = channel.ProvenanceRequests[0].CommandId;
        channel.Inject(new ReadyMessage("0.0.1+stale", "h")); // the dropped session's leftover: at most one more resend, never anything else

        await PumpAsync(time, run);
        // Replies to the old id arriving after success are dropped, not mistaken for an answer to apply_components or power_off.
        channel.Inject(new ProvenanceMessage(old, "Windows 11 Pro", "1", "0.0.1+stale", false, false));
        channel.Inject(new CommandResultMessage(old, false, "late"));

        Assert.Equal([("win11-edit", 0)], kit.Store.Seals);
        Assert.NotEmpty(kit.Guest.AppliedComponentIds); // apply_components went through
    }
}

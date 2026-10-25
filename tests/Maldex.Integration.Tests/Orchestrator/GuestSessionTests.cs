using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Orchestrator.Templates;
using Microsoft.Extensions.Time.Testing;
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

    /// <summary>A guest whose provenance answers (real-time protection off) take <paramref name="roundTrip"/> of fake time each.</summary>
    private sealed class SlowGuestChannel(TimeProvider time, TimeSpan roundTrip) : IAgentChannel
    {
        private readonly Channel<AgentMessage> _incoming = Channel.CreateUnbounded<AgentMessage>();
        public int Violations => 0;

        public Task SendAsync(AgentCommand command, CancellationToken ct)
        {
            if (command is ProvenanceCommand p)
                _ = Task.Delay(roundTrip, time).ContinueWith(_ =>
                    _incoming.Writer.TryWrite(new ProvenanceMessage(p.CommandId, "Windows 11 Pro", "26300.1", "1.0.0+aaaaaaaaaaaa", false, false)), TaskScheduler.Default);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<AgentMessage> ReadAsync(CancellationToken ct) => _incoming.Reader.ReadAllAsync(ct);

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A fake clock whose wall time can jump (<see cref="Jump"/>) while its timestamps and timers run on.</summary>
    private sealed class WallJumpClock(FakeTimeProvider inner) : TimeProvider
    {
        public TimeSpan Jump { get; set; }
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Jump;
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => inner.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>The default settle options (60 s window, a reading every 5 s), with no resends to blur the reading count.</summary>
    private static TemplateJobOptions SettleOptions() => new() { ProvenanceRetryInterval = TimeSpan.FromMinutes(5), ProvenanceTimeout = TimeSpan.FromMinutes(1) };

    private static async Task<T> PumpAsync<T>(FakeTimeProvider time, Task<T> run)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!run.IsCompleted)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), "the readings did not finish within 60 s of real time");
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1, Ct);
        }
        return await run;
    }

    [Theory]
    [InlineData(3)] // a slow WMI read right after boot: the readings keep their 5 s cadence
    [InlineData(7)] // slower than the interval: the next reading starts at once
    public async Task SlowReadings_KeepTheirCadence_SoAGuestWithDefenderOffSettlesOff(int roundTripSeconds)
    {
        var time = new FakeTimeProvider();
        await using var channel = new SlowGuestChannel(time, TimeSpan.FromSeconds(roundTripSeconds));
        await using var session = new GuestSession(channel, time);

        var settled = await PumpAsync(time, session.AskSettledProvenanceAsync(SettleOptions(), TimeSpan.FromMinutes(1), time, Ct));

        Assert.Equal(DefenderSettlement.Off, settled.Defender);
    }

    [Fact]
    public async Task AWallClockJump_DoesNotShortenTheWindow()
    {
        var fake = new FakeTimeProvider();
        var time = new WallJumpClock(fake);
        await using var channel = new ScriptedChannel();
        await using var session = new GuestSession(channel, time);

        var run = session.AskSettledProvenanceAsync(SettleOptions(), TimeSpan.FromMinutes(1), time, Ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (channel.Requests.Count < 2) // the second reading is under way: the first one's time is taken
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "no second reading");
            fake.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1, Ct);
        }
        time.Jump = TimeSpan.FromHours(1); // NTP steps the wall clock forward an hour
        var settled = await PumpAsync(fake, run);

        Assert.Equal(DefenderSettlement.Off, settled.Defender);
        Assert.True(channel.Requests.Count >= 8, $"only {channel.Requests.Count} readings");
    }
}

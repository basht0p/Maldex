using Maldex.Agent;
using Maldex.Agent.Collectors;
using Maldex.Agent.Commands;
using Maldex.Core.Agent;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Agent.Tests;

public class AgentHostTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static AgentHost MakeHost(ChannelPair pair, IEventSource source, RecordingUrlOpener? url = null, CancellationTokenSource? stopCts = null)
    {
        var dispatcher = new CommandDispatcher(new NoopSampleOpener(), url ?? new RecordingUrlOpener(), () => stopCts?.Cancel());
        return new AgentHost(pair.GuestSide, source, dispatcher, new FakeTimeProvider());
    }

    [Fact]
    public async Task Host_SendsReadyFirst()
    {
        var pair = new ChannelPair();
        using var cts = new CancellationTokenSource(Limit);
        var run = MakeHost(pair, new NullEventSource()).RunAsync(cts.Token);

        var ready = Assert.IsType<ReadyMessage>(await pair.NextAsync());
        Assert.Equal(Environment.MachineName, ready.Hostname);
        Assert.False(string.IsNullOrWhiteSpace(ready.AgentVersion));
        Assert.Equal(Environment.ProcessId, ready.Pid); // the host scopes dropped files to children of this pid

        pair.CloseHostSide();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Host_ForwardsEventSourceMessages_AsFrames()
    {
        var pair = new ChannelPair();
        var at = DateTimeOffset.UnixEpoch;
        var events = new AgentMessage[]
        {
            new ProcessStartedMessage(at, 10, 4, @"C:\a.exe", "a.exe"),
            new FileEventMessage(at, 10, "create", @"C:\x.txt"),
            new DnsEventMessage(at, 10, "example.test"),
        };
        using var cts = new CancellationTokenSource(Limit);
        var run = MakeHost(pair, new ScriptedSource(events)).RunAsync(cts.Token);

        Assert.IsType<ReadyMessage>(await pair.NextAsync());
        Assert.Equal(events[0], await pair.NextAsync());
        Assert.Equal(events[1], await pair.NextAsync());
        Assert.Equal(events[2], await pair.NextAsync());

        pair.CloseHostSide();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Host_CommandProducesExactlyOneResult()
    {
        var pair = new ChannelPair();
        var url = new RecordingUrlOpener();
        using var cts = new CancellationTokenSource(Limit);
        var run = MakeHost(pair, new NullEventSource(), url).RunAsync(cts.Token);
        Assert.IsType<ReadyMessage>(await pair.NextAsync());

        await pair.SendAsync(new OpenUrlCommand("u1", "http://example.test/"));
        Assert.Equal(new CommandResultMessage("u1", true), await pair.NextAsync());
        Assert.Equal(["http://example.test/"], url.Opened);

        pair.CloseHostSide();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Host_Shutdown_ReportsResult_ThenStops()
    {
        var pair = new ChannelPair();
        using var stop = new CancellationTokenSource();
        using var cts = new CancellationTokenSource(Limit);
        var run = MakeHost(pair, new NullEventSource(), stopCts: stop).RunAsync(cts.Token);
        Assert.IsType<ReadyMessage>(await pair.NextAsync());

        await pair.SendAsync(new ShutdownCommand("s1"));
        Assert.Equal(new CommandResultMessage("s1", true), await pair.NextAsync());
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Assert.True(stop.IsCancellationRequested);
    }

    [Fact]
    public async Task Host_EndOfStream_Stops()
    {
        var pair = new ChannelPair();
        using var cts = new CancellationTokenSource(Limit);
        var run = MakeHost(pair, new ScriptedSource()).RunAsync(cts.Token);
        Assert.IsType<ReadyMessage>(await pair.NextAsync());

        pair.CloseHostSide();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Host_Cancellation_Stops_WithoutThrowing()
    {
        var pair = new ChannelPair();
        using var cts = new CancellationTokenSource();
        var run = MakeHost(pair, new NullEventSource()).RunAsync(cts.Token);
        Assert.IsType<ReadyMessage>(await pair.NextAsync());

        cts.Cancel();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);
    }
}

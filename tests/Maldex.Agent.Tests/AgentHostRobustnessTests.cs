using Maldex.Agent.Collectors;
using Maldex.Agent.Commands;
using Maldex.Core.Agent;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Agent.Tests;

public class AgentHostRobustnessTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static AgentHost MakeHost(ChannelPair pair, IEventSource source) =>
        new(pair.GuestSide, source, new CommandDispatcher(new NoopSampleOpener(), new RecordingUrlOpener(), () => { }), new FakeTimeProvider());

    [Fact]
    public async Task OversizeScreenshot_IsDropped_AndHostKeepsRunning()
    {
        var pair = new ChannelPair();
        var bytes = new byte[780_000];
        new Random(7).NextBytes(bytes);
        // Random base64 contains '+', which JSON-escapes to 6 chars and pushes this past the frame cap.
        Assert.False(FrameBudget.Fits("png", bytes));
        var big = new ScreenshotMessage(DateTimeOffset.UnixEpoch, "png", Convert.ToBase64String(bytes));
        var after = new FileEventMessage(DateTimeOffset.UnixEpoch, 1, "create", "x");

        using var cts = new CancellationTokenSource(Limit);
        var run = MakeHost(pair, new ScriptedSource(big, after)).RunAsync(cts.Token);
        Assert.IsType<ReadyMessage>(await pair.NextAsync());
        Assert.Equal(after, await pair.NextAsync());

        await pair.SendAsync(new OpenUrlCommand("still-alive", "http://x/"));
        Assert.Equal(new CommandResultMessage("still-alive", true), await pair.NextAsync());
        pair.CloseHostSide();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EventFlood_WithCommands_EveryFrameParses()
    {
        var pair = new ChannelPair();
        var events = Enumerable.Range(0, 500)
            .Select(i => (AgentMessage)new FileEventMessage(DateTimeOffset.UnixEpoch, i, "create", new string('a', 2000)))
            .ToArray();
        using var cts = new CancellationTokenSource(Limit);
        var run = MakeHost(pair, new ScriptedSource(events)).RunAsync(cts.Token);

        var sender = Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++) await pair.SendAsync(new OpenUrlCommand("c" + i, "http://x/"));
        }, TestContext.Current.CancellationToken);

        int files = 0, results = 0;
        while (files < 500 || results < 50)
        {
            switch (await pair.NextAsync())
            {
                case FileEventMessage: files++; break;
                case CommandResultMessage: results++; break;
                case ReadyMessage: break;
                default: Assert.Fail("unexpected message"); break;
            }
        }
        await sender;
        pair.CloseHostSide();
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);
    }
}

using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Infrastructure.Fakes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Core.Tests.Fakes;

public sealed class ReplayAgentChannelTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _dir = Directory.CreateTempSubdirectory("maldex-replay-").FullName;
    private readonly CountingTimeProvider _time = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private static readonly VmHandle Vm = VmHandle.For(Guid.NewGuid());

    public void Dispose() => Directory.Delete(_dir, true);

    private static Task Start(Func<Task> work) => Task.Run(work, TestContext.Current.CancellationToken);

    private const string Ready = """{"delayMs":1000,"message":{"type":"ready","agentVersion":"1","hostname":"h"}}""";
    private const string Proc = """{"delayMs":1000,"message":{"type":"process_started","at":"2000-01-01T00:00:00Z","pid":10,"parentPid":1,"imagePath":"C:\\a.exe","commandLine":"a.exe"}}""";
    private const string File1 = """{"delayMs":1000,"message":{"type":"file","at":"2000-01-01T00:00:00Z","pid":10,"operation":"write","path":"C:\\f.txt"}}""";

    private Task<IAgentChannel> Open(string[] lines, double speed = 1.0, bool holdOpen = false)
    {
        File.WriteAllLines(Path.Combine(_dir, "t.jsonl"), lines);
        var opts = new ReplayOptions { Directory = _dir, DefaultFile = "t.jsonl", Speed = speed, HoldOpen = holdOpen };
        return new FakeAgentChannelFactory(new StaticMonitor(opts), _time).ConnectAsync(Vm, TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.Elapsed < Timeout, failure);
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }

    private async Task Pump(Task reader, TimeSpan step)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!reader.IsCompleted)
        {
            Assert.True(sw.Elapsed < Timeout, "Replay did not complete within the timeout.");
            _time.Advance(step);
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        await reader.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    private async Task<List<AgentMessage>> Drain(IAgentChannel ch)
    {
        var list = new List<AgentMessage>();
        var reader = Start(async () =>
        {
            await foreach (var m in ch.ReadAsync(TestContext.Current.CancellationToken)) lock (list) list.Add(m);
        });
        await Pump(reader, TimeSpan.FromMilliseconds(250));
        return list;
    }

    [Fact]
    public async Task Replay_YieldsMessagesInOrder_WithScaledDelays()
    {
        await using var ch = await Open([Ready, Proc, File1], speed: 2.0).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var start = _time.GetUtcNow();
        var firstAt = TimeSpan.Zero;
        var seen = new List<(AgentMessage M, DateTimeOffset Now)>();
        var reader = Start(async () =>
        {
            await foreach (var m in ch.ReadAsync(TestContext.Current.CancellationToken)) lock (seen) seen.Add((m, _time.GetUtcNow()));
        });
        // Advance the fake clock only once the replay has registered the delay it is waiting on, so cold-start
        // latency cannot shift the schedule: each message must arrive exactly one scaled delay after the previous.
        for (var i = 0; i < 3; i++)
        {
            await WaitUntilAsync(() => _time.TimersCreated >= i + 1, "The replay did not register its delay.");
            _time.Advance(TimeSpan.FromMilliseconds(500));
            await WaitUntilAsync(() => { lock (seen) return seen.Count >= i + 1; }, "The replay did not yield the next message.");
        }
        await reader.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Collection(seen.Select(s => s.M), m => Assert.IsType<ReadyMessage>(m), m => Assert.IsType<ProcessStartedMessage>(m), m => Assert.IsType<FileEventMessage>(m));
        // 1000ms at speed 2 = 500ms between messages (unscaled would be 1000ms).
        firstAt = seen[0].Now - start;
        Assert.Equal(TimeSpan.FromMilliseconds(500), firstAt);
        Assert.Equal(TimeSpan.FromMilliseconds(500), seen[1].Now - seen[0].Now);
        Assert.Equal(TimeSpan.FromMilliseconds(500), seen[2].Now - seen[1].Now);
        // At is rewritten to the (fake) clock.
        var p = Assert.IsType<ProcessStartedMessage>(seen[1].M);
        Assert.True(p.At >= start, "At must be rewritten to time provider now");
        Assert.Equal(0, ch.Violations);
    }

    [Fact]
    public async Task Replay_HoldOpen_DoesNotComplete_UntilDisposed()
    {
        var ch = await Open([Ready], holdOpen: true).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var got = new List<AgentMessage>();
        var reader = Start(async () => { await foreach (var m in ch.ReadAsync(TestContext.Current.CancellationToken)) lock (got) got.Add(m); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            lock (got) if (got.Count == 1) break;
            Assert.True(sw.Elapsed < Timeout, "No message within the timeout.");
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        for (var i = 0; i < 20; i++) { _time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(1, TestContext.Current.CancellationToken); }
        Assert.False(reader.IsCompleted);
        await ch.DisposeAsync();
        await reader.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ch.SendAsync(new ShutdownCommand("c"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Replay_NotHoldOpen_CompletesAfterLast()
    {
        await using var ch = await Open([Ready, Proc]).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var list = await Drain(ch);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task Replay_MalformedLine_CountsViolation_AndContinues()
    {
        const string badPid = """{"delayMs":10,"message":{"type":"process_started","at":"2000-01-01T00:00:00Z","pid":-5,"parentPid":1,"imagePath":"x","commandLine":"x"}}""";
        const string noMessage = """{"delayMs":10}""";
        await using var ch = await Open([Ready, "this is not json", badPid, noMessage, File1]).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var list = await Drain(ch);
        Assert.Collection(list, m => Assert.IsType<ReadyMessage>(m), m => Assert.IsType<FileEventMessage>(m));
        Assert.Equal(3, ch.Violations);
    }

    [Fact]
    public async Task Replay_TenViolations_EndsTheChannel()
    {
        var lines = Enumerable.Repeat("garbage", 10).Append(Ready).ToArray();
        await using var ch = await Open(lines).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var list = await Drain(ch);
        Assert.Empty(list);
        Assert.Equal(10, ch.Violations);
    }

    [Fact]
    public async Task Replay_RecordsSentCommands()
    {
        await using var ch = await Open([Ready]).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await ch.SendAsync(new OpenUrlCommand("c1", "http://x"), TestContext.Current.CancellationToken);
        var rc = (ReplayAgentChannel)ch;
        Assert.Equal("c1", Assert.Single(rc.SentCommands).CommandId);
    }

    [Fact]
    public async Task Factory_ReadsOptionsOnEveryConnect()
    {
        File.WriteAllLines(Path.Combine(_dir, "a.jsonl"), [Ready]);
        File.WriteAllLines(Path.Combine(_dir, "b.jsonl"), [Ready, Proc]);
        var opts = new ReplayOptions { Directory = _dir, DefaultFile = "a.jsonl", HoldOpen = false };
        var mon = new StaticMonitor(opts);
        var f = new FakeAgentChannelFactory(mon, _time);
        await using (var c1 = await f.ConnectAsync(Vm, TestContext.Current.CancellationToken)) Assert.Single(await Drain(c1));
        mon.Value = new ReplayOptions { Directory = _dir, DefaultFile = "b.jsonl", HoldOpen = false };
        await using (var c2 = await f.ConnectAsync(Vm, TestContext.Current.CancellationToken)) Assert.Equal(2, (await Drain(c2)).Count);
    }

    [Fact]
    public async Task AllShippedReplays_ParseWithoutViolations()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Fakes", "Replays");
        var files = Directory.GetFiles(shipped, "*.jsonl");
        Assert.True(files.Length >= 4);
        foreach (var file in files)
        {
            var opts = new ReplayOptions { Directory = shipped, DefaultFile = Path.GetFileName(file), Speed = 1000, HoldOpen = false };
            await using var ch = await new FakeAgentChannelFactory(new StaticMonitor(opts), _time).ConnectAsync(Vm, TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);
            var list = await Drain(ch);
            Assert.NotEmpty(list);
            Assert.IsType<ReadyMessage>(list[0]);
            Assert.True(ch.Violations == 0, $"{file}: {ch.Violations} violations");
        }
    }

    [Fact]
    public async Task DefaultReplay_HasReadyExplorerNotepadFileDnsScreenshot_InOrder()
    {
        var opts = new ReplayOptions { Speed = 1000, HoldOpen = false };
        await using var ch = await new FakeAgentChannelFactory(new StaticMonitor(opts), _time).ConnectAsync(Vm, TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var list = await Drain(ch);
        Assert.Equal(6, list.Count);
        Assert.IsType<ReadyMessage>(list[0]);
        var explorer = Assert.IsType<ProcessStartedMessage>(list[1]);
        Assert.EndsWith("explorer.exe", explorer.ImagePath);
        var notepad = Assert.IsType<ProcessStartedMessage>(list[2]);
        Assert.EndsWith("notepad.exe", notepad.ImagePath);
        Assert.Equal(explorer.Pid, notepad.ParentPid);
        Assert.IsType<FileEventMessage>(list[3]);
        Assert.IsType<DnsEventMessage>(list[4]);
        Assert.IsType<ScreenshotMessage>(list[5]);
    }

    [Fact]
    public async Task ReadAsync_AfterDispose_YieldsNothing_WithoutThrowing()
    {
        var ch = await Open([Ready], holdOpen: true).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await ch.DisposeAsync();
        var list = new List<AgentMessage>();
        await foreach (var m in ch.ReadAsync(TestContext.Current.CancellationToken)) list.Add(m);
        Assert.Empty(list);
        await ch.DisposeAsync(); // idempotent
    }

    [Fact]
    public async Task DisposeAsync_DuringReadEnumeration_CompletesItWithoutException()
    {
        var ch = await Open([Ready], holdOpen: true).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var got = new List<AgentMessage>();
        var reader = Start(async () => { await foreach (var m in ch.ReadAsync(TestContext.Current.CancellationToken)) lock (got) got.Add(m); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            lock (got) if (got.Count == 1) break;
            Assert.True(sw.Elapsed < Timeout, "No message within the timeout.");
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        await Task.Delay(20, TestContext.Current.CancellationToken); // let the read block on the pipe
        await ch.DisposeAsync();
        await reader.WaitAsync(Timeout, TestContext.Current.CancellationToken); // faults if an exception escaped
        Assert.True(reader.IsCompletedSuccessfully);
    }

    [Fact]
    public void ShippedScreenshots_AreDecodableBaselineJpegs()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Fakes", "Replays");
        var count = 0;
        foreach (var file in Directory.GetFiles(shipped, "*.jsonl"))
            foreach (var line in File.ReadLines(file).Where(l => l.Contains("\"screenshot\"")))
            {
                var msg = Assert.IsType<ScreenshotMessage>(System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("message").Deserialize<AgentMessage>(AgentJson.Options));
                var b = Convert.FromBase64String(msg.ImageBase64);
                Assert.True(b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8, "missing SOI");
                Assert.True(b[^2] == 0xFF && b[^1] == 0xD9, "missing EOI");
                Assert.True(b.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 }) > 0, "missing SOF0 (baseline)");
                count++;
            }
        Assert.True(count >= 1);
    }

    /// <summary>A fake clock that counts the timers (delays) created on it.</summary>
    private sealed class CountingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private int _timers;
        public int TimersCreated => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Counted only once the timer is registered: a test that advances the clock as soon as it sees the count must
            // never advance before the timer exists (it would then be due one delay after the advance and never fire).
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            return timer;
        }
    }

    private sealed class StaticMonitor(ReplayOptions value) : IOptionsMonitor<ReplayOptions>
    {
        public ReplayOptions Value { get; set; } = value;
        public ReplayOptions CurrentValue => Value;
        public ReplayOptions Get(string? name) => Value;
        public IDisposable? OnChange(Action<ReplayOptions, string?> listener) => null;
    }
}

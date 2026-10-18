using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Fakes;

public sealed class FakeAgentChannelFactory(IOptionsMonitor<ReplayOptions> options, TimeProvider time) : IAgentChannelFactory
{
    public async Task<IAgentChannel> ConnectAsync(VmHandle vm, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var path = Path.Combine(o.ResolveDirectory(), o.DefaultFile);
        var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
        return new ReplayAgentChannel(lines, o.Speed, o.HoldOpen, time);
    }
}

/// <summary>
/// Replays a recorded agent session. Messages are framed into an in-memory pipe and read back
/// through <see cref="AgentFrameReader"/>, so fake mode exercises the real protocol code.
/// </summary>
public sealed class ReplayAgentChannel : IAgentChannel
{
    private readonly Pipe _pipe = new();
    private readonly AgentFrameReader _reader;
    private readonly AgentViolationTracker _tracker = new(10);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _producer;
    private readonly List<AgentCommand> _sent = [];
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly double _speed;
    private readonly bool _holdOpen;
    private int _disposed;
    private int _activeReads;
    private bool _closing;
    private bool _readerCompleted;

    public ReplayAgentChannel(IReadOnlyList<string> lines, double speed, bool holdOpen, TimeProvider time)
    {
        _time = time;
        _speed = speed > 0 ? speed : 1.0;
        _holdOpen = holdOpen;
        _reader = new AgentFrameReader(_pipe.Reader.AsStream());
        _producer = Task.Run(() => ProduceAsync(lines.ToArray()));
    }

    public int Violations => _tracker.Count;

    public IReadOnlyList<AgentCommand> SentCommands
    {
        get { lock (_gate) return _sent.ToArray(); }
    }

    private async ValueTask CompleteReaderAsync()
    {
        lock (_gate) { if (_readerCompleted) return; _readerCompleted = true; }
        await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
    }

    public async IAsyncEnumerable<AgentMessage> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        lock (_gate) _activeReads++;
        try
        {
            await foreach (var m in ReadCoreAsync(ct).ConfigureAwait(false)) yield return m;
        }
        finally
        {
            bool complete;
            lock (_gate) { _activeReads--; complete = _closing && _activeReads == 0; }
            if (complete) await CompleteReaderAsync().ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<AgentMessage> ReadCoreAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(ct).ConfigureAwait(false);
            switch (result)
            {
                case FrameReadResult.Frame f:
                    var invalid = AgentMessageValidator.Validate(f.Message);
                    if (invalid is not null)
                    {
                        if (_tracker.Record(invalid)) yield break;
                        continue;
                    }
                    yield return f.Message;
                    break;
                case FrameReadResult.Dropped d:
                    if (_tracker.Record(d.Reason)) yield break;
                    break;
                default: // Fatal or EndOfStream
                    yield break;
            }
        }
    }

    public Task SendAsync(AgentCommand command, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate) _sent.Add(command);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        await _producer.ConfigureAwait(false);
        lock (_gate) { _closing = true; if (_activeReads > 0) return; }
        await CompleteReaderAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task ProduceAsync(string[] lines)
    {
        var ct = _cts.Token;
        Exception? error = null;
        try
        {
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var (delayMs, message) = Parse(line);
                if (delayMs > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs / _speed), _time, ct).ConfigureAwait(false);
                var body = message is null
                    ? Encoding.UTF8.GetBytes(line)
                    : JsonSerializer.SerializeToUtf8Bytes(Stamp(message, _time.GetUtcNow()), AgentJson.Options);
                await WriteFrameAsync(body, ct).ConfigureAwait(false);
            }

            if (_holdOpen) await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        finally
        {
            await _pipe.Writer.CompleteAsync(error).ConfigureAwait(false);
        }
    }

    private async Task WriteFrameAsync(byte[] body, CancellationToken ct)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        await _pipe.Writer.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    /// <summary>A line that is not a well-formed {delayMs, message} yields a null message (sent raw, so the reader drops it).</summary>
    private static (int DelayMs, AgentMessage? Message) Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (0, null);
            var delay = root.TryGetProperty("delayMs", out var d) && d.TryGetInt32(out var ms) && ms > 0 ? ms : 0;
            if (!root.TryGetProperty("message", out var m)) return (delay, null);
            return (delay, m.Deserialize<AgentMessage>(AgentJson.Options));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return (0, null);
        }
    }

    private static AgentMessage Stamp(AgentMessage m, DateTimeOffset now) => m switch
    {
        ProcessStartedMessage x => x with { At = now },
        ProcessExitedMessage x => x with { At = now },
        FileEventMessage x => x with { At = now },
        RegistryEventMessage x => x with { At = now },
        NetworkEventMessage x => x with { At = now },
        DnsEventMessage x => x with { At = now },
        ScreenshotMessage x => x with { At = now },
        _ => m,
    };
}

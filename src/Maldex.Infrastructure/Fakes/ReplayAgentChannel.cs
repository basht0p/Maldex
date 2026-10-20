using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Fakes;

public sealed class FakeAgentChannelFactory(IOptionsMonitor<ReplayOptions> options, TimeProvider time, FakeGuest? guest = null) : IAgentChannelFactory
{
    public async Task<IAgentChannel> ConnectAsync(VmHandle vm, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var path = Path.Combine(o.ResolveDirectory(), o.DefaultFile);
        var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
        return new ReplayAgentChannel(lines, o.Speed, o.HoldOpen, time, guest);
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
    private readonly Task _pump;
    // Every frame (replayed or answered) goes through one queue and one writer, so frames never interleave.
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly FakeGuest? _guest;
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<AgentCommand> _sent = [];
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly double _speed;
    private readonly bool _holdOpen;
    private int _disposed;
    private int _activeReads;
    private bool _closing;
    private bool _readerCompleted;

    /// <param name="guest">
    /// When given, the channel behaves like a guest during bulk transfers (see <see cref="FakeGuest"/>) and, unless it holds the
    /// stream open, keeps it open until the host has sent its open command so that transfer answers can still be delivered.
    /// </param>
    public ReplayAgentChannel(IReadOnlyList<string> lines, double speed, bool holdOpen, TimeProvider time, FakeGuest? guest = null)
    {
        _guest = guest;
        _time = time;
        _speed = speed > 0 ? speed : 1.0;
        _holdOpen = holdOpen;
        _reader = new AgentFrameReader(_pipe.Reader.AsStream());
        _pump = Task.Run(PumpAsync);
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
        _cts.Dispose(); // the producer has finished by the time the reader is completed
    }

    public async IAsyncEnumerable<AgentMessage> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        lock (_gate)
        {
            if (_closing) yield break;
            _activeReads++;
        }
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
        if (_guest is not null) Respond(command);
        return Task.CompletedTask;
    }

    /// <summary>Plays the guest's side of a command: acknowledge sample chunks, serve collected files, then note that the run has started.</summary>
    private void Respond(AgentCommand command)
    {
        switch (command)
        {
            case PutFileChunkCommand c:
                Enqueue(_guest!.RejectSampleChunkIndex == c.Index
                    ? new CommandResultMessage(c.CommandId, false, "chunk rejected by the fake guest")
                    : new CommandResultMessage(c.CommandId, true));
                break;
            case CollectFileCommand c:
                if (!_guest!.TryGetFile(c.Path, out var file))
                {
                    Enqueue(new CommandResultMessage(c.CommandId, false, "file not found"));
                    break;
                }
                var sha = file.ClaimedSha256 ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file.Data)).ToLowerInvariant();
                var total = file.ClaimedTotalBytes ?? file.Data.Length;
                var count = Math.Max(1, (file.Data.Length + AgentLimits.ChunkBytes - 1) / AgentLimits.ChunkBytes);
                for (var i = 0; i < count; i++)
                {
                    var offset = i * AgentLimits.ChunkBytes;
                    var part = file.Data.AsSpan(offset, Math.Min(AgentLimits.ChunkBytes, file.Data.Length - offset));
                    Enqueue(new FileChunkMessage(_time.GetUtcNow(), c.CommandId, i, count, total, sha, c.Path, Convert.ToBase64String(part)));
                }
                Enqueue(new CommandResultMessage(c.CommandId, true));
                break;
            case OpenSampleCommand c:
                Enqueue(new CommandResultMessage(c.CommandId, true));
                _opened.TrySetResult();
                break;
            case OpenUrlCommand:
                _opened.TrySetResult();
                break;
        }
    }

    private void Enqueue(AgentMessage message) =>
        _outbox.Writer.TryWrite(Frame(JsonSerializer.SerializeToUtf8Bytes(message, AgentJson.Options)));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        await _producer.ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        lock (_gate) { _closing = true; if (_activeReads > 0) return; }
        await CompleteReaderAsync().ConfigureAwait(false);
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
                _outbox.Writer.TryWrite(Frame(body));
            }

            if (_holdOpen) await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            else if (_guest is not null) await _opened.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        finally
        {
            _outbox.Writer.TryComplete(error);
        }
    }

    private async Task PumpAsync()
    {
        var ct = _cts.Token;
        Exception? error = null;
        try
        {
            await foreach (var frame in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await _pipe.Writer.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        finally
        {
            await _pipe.Writer.CompleteAsync(error).ConfigureAwait(false);
        }
    }

    private static byte[] Frame(byte[] body)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
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

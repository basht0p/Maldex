using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;

namespace Maldex.Infrastructure.Agent;

/// <summary>
/// Host side of the virtio-serial agent channel. Reads untrusted frames through
/// <see cref="AgentFrameReader"/> and applies the same completion and violation semantics as
/// <c>ReplayAgentChannel</c>.
/// </summary>
public sealed class VirtioSerialAgentChannel : IAgentChannel
{
    private readonly Stream _stream;
    private readonly AgentFrameReader _reader;
    private readonly AgentViolationTracker _tracker = new(10);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private int _disposed;

    public VirtioSerialAgentChannel(Stream stream)
    {
        _stream = stream;
        _reader = new AgentFrameReader(stream);
    }

    public int Violations => _tracker.Count;

    public async IAsyncEnumerable<AgentMessage> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            var result = await ReadFrameAsync(ct).ConfigureAwait(false);
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

    /// <summary>Disposal or a broken transport reads as end of stream; caller cancellation still throws.</summary>
    private async ValueTask<FrameReadResult> ReadFrameAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0) return FrameReadResult.EndOfStream.Instance;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
            return await _reader.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return FrameReadResult.EndOfStream.Instance;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            ct.ThrowIfCancellationRequested();
            return FrameReadResult.EndOfStream.Instance;
        }
    }

    public async Task SendAsync(AgentCommand command, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            // The caller token is honoured only while waiting for the send slot.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
            await _sendGate.WaitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(VirtioSerialAgentChannel));
        }

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            // Once a frame starts it must finish: a cancel mid-frame would desync the guest's reader.
            // Only channel disposal (or a transport failure, which breaks the channel) interrupts it.
            await AgentFrameWriter.WriteAsync(_stream, command, _closed.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
            await DisposeAsync().ConfigureAwait(false); // possibly a partial frame on the wire: channel is unusable
            throw new ObjectDisposedException(nameof(VirtioSerialAgentChannel), ex);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _closed.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        // _closed and _sendGate are intentionally not disposed: a read or send may still be unwinding.
    }
}

using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Text;

namespace Maldex.Orchestrator;

/// <summary>A sample could not be delivered into the guest. The message is safe to store as the task's failure reason.</summary>
public sealed class SampleDeliveryException(string message) : Exception("sample delivery failed: " + message);

/// <summary>The delivered sample could not be opened (the run is already Running). The message is safe to store.</summary>
public sealed class SampleOpenException(string message) : Exception("sample open failed: " + message);

/// <summary>The analyst asked to stop while the sample was being delivered.</summary>
public sealed class DeliveryStoppedException() : Exception("stopped by analyst during sample delivery");

/// <summary>
/// Sends a sample into the guest as 512 KiB chunks (each acknowledged before the next is sent) in Preparing; once the run is
/// Running, <see cref="OpenAsync"/> asks the agent to open it (so the live view sees the start).
/// </summary>
public sealed class SampleDelivery(TimeProvider time, TimeSpan? chunkTimeout = null, TimeSpan? pollInterval = null)
{
    public static readonly TimeSpan DefaultChunkTimeout = TimeSpan.FromSeconds(30);
    private const int MaxViolations = 10;

    private readonly TimeSpan _chunkTimeout = chunkTimeout ?? DefaultChunkTimeout;
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(1);

    /// <param name="ch">The agent channel; only <see cref="IAgentChannel.SendAsync"/> (and <c>Violations</c>) is used.</param>
    /// <param name="inbox">Where the run's read loop delivers the agent's replies. Opened here, so nothing else may hold it.</param>
    /// <param name="blobKey">Becomes the transfer id (and later the <c>open_sample</c> blob key).</param>
    /// <param name="size">Exact length of <paramref name="blob"/>.</param>
    /// <param name="sha256">Hex SHA-256 of the whole sample; the agent verifies it before opening anything.</param>
    /// <param name="stopRequested">Polled about once per poll interval, also while waiting for an answer; true ends delivery with <see cref="DeliveryStoppedException"/>.</param>
    /// <exception cref="SampleDeliveryException">Any failure: too large, unreadable, refused, timed out or disconnected.</exception>
    public async Task DeliverAsync(IAgentChannel ch, TransferInbox inbox, string blobKey, string fileName, Stream blob, long size, string sha256, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? stopRequested = null)
    {
        await SendChunksAsync(ch, inbox, blobKey, blob, size, sha256, AgentLimits.MaxInboundBytes, "sample", stopRequested, m => new SampleDeliveryException(m), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one Maldex component file (up to <see cref="AgentLimits.MaxComponentBytes"/>) the same way: chunked, each chunk acknowledged.
    /// <paramref name="transferId"/> must start with <see cref="AgentLimits.ComponentTransferPrefix"/>.
    /// </summary>
    /// <param name="fail">Builds the exception for any failure (too large, refused, timed out, disconnected).</param>
    public async Task DeliverComponentAsync(IAgentChannel ch, TransferInbox inbox, string transferId, byte[] data, string sha256, Func<string, Exception> fail, CancellationToken ct)
    {
        if (!AgentLimits.IsComponentTransfer(transferId)) throw fail(AgentLimits.NotComponentTransferMessage(transferId));
        using var stream = new MemoryStream(data, writable: false);
        await SendChunksAsync(ch, inbox, transferId, stream, data.Length, sha256, AgentLimits.MaxComponentBytes, "component", null, fail, ct).ConfigureAwait(false);
    }

    /// <summary>Sends <paramref name="command"/> and waits up to <paramref name="timeout"/> for its command result.</summary>
    /// <exception cref="Exception">Whatever <paramref name="fail"/> builds, for a refusal, a timeout or a disconnect.</exception>
    public async Task CommandAsync(IAgentChannel ch, TransferInbox inbox, AgentCommand command, string what, TimeSpan timeout, Func<string, Exception> fail, CancellationToken ct)
    {
        using var lease = inbox.Open();
        await SendAndAwaitAsync(ch, lease.Reader, command, what, null, fail, ct, timeout).ConfigureAwait(false);
    }

    private async Task SendChunksAsync(IAgentChannel ch, TransferInbox inbox, string transferId, Stream blob, long size, string sha256, long maxBytes, string kind,
        Func<CancellationToken, Task<bool>>? stopRequested, Func<string, Exception> fail, CancellationToken ct)
    {
        if (size <= 0) throw fail($"the {kind} is empty");
        if (size > maxBytes) throw fail($"the {kind} is {size} bytes; the limit is {maxBytes}");
        var count = (int)((size + AgentLimits.ChunkBytes - 1) / AgentLimits.ChunkBytes);
        var sha = sha256.ToLowerInvariant();

        using var lease = inbox.Open();
        var buffer = new byte[AgentLimits.ChunkBytes];
        for (var i = 0; i < count; i++)
        {
            var length = (int)Math.Min(AgentLimits.ChunkBytes, size - (long)i * AgentLimits.ChunkBytes);
            try
            {
                await blob.ReadExactlyAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                throw fail($"the stored {kind} is shorter than its recorded size");
            }

            var command = new PutFileChunkCommand(NewId(), transferId, i, count, size, sha, Convert.ToBase64String(buffer.AsSpan(0, length)));
            await SendAndAwaitAsync(ch, lease.Reader, command, $"chunk {i + 1} of {count}", stopRequested, fail, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Asks the agent to open the delivered sample and waits for its answer.</summary>
    /// <param name="stopRequested">Polled while waiting; true ends the wait with <see cref="DeliveryStoppedException"/>.</param>
    /// <exception cref="SampleOpenException">Refused, timed out or disconnected.</exception>
    public async Task OpenAsync(IAgentChannel ch, TransferInbox inbox, string blobKey, string fileName, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? stopRequested = null)
    {
        using var lease = inbox.Open();
        await SendAndAwaitAsync(ch, lease.Reader, new OpenSampleCommand(NewId(), fileName, blobKey), "open", stopRequested, m => new SampleOpenException(m), ct).ConfigureAwait(false);
    }

    private async Task SendAndAwaitAsync(IAgentChannel ch, ChannelReader<AgentMessage> replies, AgentCommand command, string what,
        Func<CancellationToken, Task<bool>>? stopRequested, Func<string, Exception> fail, CancellationToken ct, TimeSpan? timeoutOverride = null)
    {
        var limit = timeoutOverride ?? _chunkTimeout;
        // Checked before the timeout starts: the timeout bounds the agent's answer, not our own database read.
        if (stopRequested is not null && await stopRequested(ct).ConfigureAwait(false)) throw new DeliveryStoppedException();
        using var timeout = new CancellationTokenSource(limit, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await ch.SendAsync(command, linked.Token).ConfigureAwait(false);
            while (true)
            {
                var read = replies.ReadAsync(linked.Token).AsTask();
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
                        throw new DeliveryStoppedException();
                    }
                }

                var message = await read.ConfigureAwait(false);
                if (message is not CommandResultMessage result || result.CommandId != command.CommandId) continue;
                if (!result.Success)
                    throw fail($"{what}: the agent refused it ({GuestText.Clean(result.Error, 200)})");
                return;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw fail($"{what}: no answer from the agent within {limit.TotalSeconds:0} s");
        }
        catch (ChannelClosedException)
        {
            throw fail(ch.Violations >= MaxViolations ? "agent protocol violations" : $"{what}: the agent disconnected");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw fail($"{what}: {GuestText.Clean(ex.Message, 200)}");
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}

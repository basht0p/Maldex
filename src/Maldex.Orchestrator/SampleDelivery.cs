using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Text;

namespace Maldex.Orchestrator;

/// <summary>A sample could not be delivered into the guest. The message is safe to store as the task's failure reason.</summary>
public sealed class SampleDeliveryException(string message) : Exception("sample delivery failed: " + message);

/// <summary>
/// Sends a sample into the guest as 512 KiB chunks (each acknowledged before the next is sent), then asks the agent to open it.
/// </summary>
public sealed class SampleDelivery(TimeProvider time, TimeSpan? chunkTimeout = null)
{
    public static readonly TimeSpan DefaultChunkTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _chunkTimeout = chunkTimeout ?? DefaultChunkTimeout;

    /// <param name="ch">The agent channel; only <see cref="IAgentChannel.SendAsync"/> is used.</param>
    /// <param name="inbox">Where the run's read loop delivers the agent's replies. Opened here, so nothing else may hold it.</param>
    /// <param name="blobKey">Becomes the transfer id and the <c>open_sample</c> blob key.</param>
    /// <param name="size">Exact length of <paramref name="blob"/>.</param>
    /// <param name="sha256">Hex SHA-256 of the whole sample; the agent verifies it before opening anything.</param>
    /// <exception cref="SampleDeliveryException">Any failure: too large, unreadable, refused, timed out or disconnected.</exception>
    public async Task DeliverAsync(IAgentChannel ch, TransferInbox inbox, string blobKey, string fileName, Stream blob, long size, string sha256, CancellationToken ct)
    {
        if (size <= 0) throw new SampleDeliveryException("the sample is empty");
        if (size > AgentLimits.MaxInboundBytes) throw new SampleDeliveryException($"the sample is {size} bytes; the limit is {AgentLimits.MaxInboundBytes}");
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
                throw new SampleDeliveryException("the stored sample is shorter than its recorded size");
            }

            var command = new PutFileChunkCommand(NewId(), blobKey, i, count, size, sha, Convert.ToBase64String(buffer.AsSpan(0, length)));
            await SendAndAwaitAsync(ch, lease.Reader, command, $"chunk {i + 1} of {count}", ct).ConfigureAwait(false);
        }

        await SendAndAwaitAsync(ch, lease.Reader, new OpenSampleCommand(NewId(), fileName, blobKey), "open", ct).ConfigureAwait(false);
    }

    private async Task SendAndAwaitAsync(IAgentChannel ch, ChannelReader<AgentMessage> replies, AgentCommand command, string what, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_chunkTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await ch.SendAsync(command, linked.Token).ConfigureAwait(false);
            while (true)
            {
                var message = await replies.ReadAsync(linked.Token).ConfigureAwait(false);
                if (message is not CommandResultMessage result || result.CommandId != command.CommandId) continue;
                if (!result.Success)
                    throw new SampleDeliveryException($"{what}: the agent refused it ({GuestText.Clean(result.Error, 200)})");
                return;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new SampleDeliveryException($"{what}: no answer from the agent within {_chunkTimeout.TotalSeconds:0} s");
        }
        catch (ChannelClosedException)
        {
            throw new SampleDeliveryException($"{what}: the agent disconnected");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new SampleDeliveryException($"{what}: {GuestText.Clean(ex.Message, 200)}");
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}

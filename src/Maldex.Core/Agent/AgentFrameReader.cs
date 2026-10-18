using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace Maldex.Core.Agent;

public abstract record FrameReadResult
{
    private FrameReadResult() { }

    public sealed record Frame(AgentMessage Message) : FrameReadResult;
    public sealed record Dropped(string Reason) : FrameReadResult;
    public sealed record Fatal(string Reason) : FrameReadResult;
    public sealed record EndOfStream : FrameReadResult
    {
        public static EndOfStream Instance { get; } = new();
    }
}

/// <summary>
/// Reads 4-byte big-endian length-prefixed UTF-8 JSON frames from an untrusted guest.
/// Never throws on hostile input (cancellation and underlying I/O errors excepted).
/// </summary>
public sealed class AgentFrameReader(Stream stream)
{
    public const int MaxFrameBytes = 1_048_576;

    public async ValueTask<FrameReadResult> ReadAsync(CancellationToken ct)
    {
        var header = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return FrameReadResult.EndOfStream.Instance;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > MaxFrameBytes)
            return new FrameReadResult.Fatal($"Frame length {length} exceeds maximum {MaxFrameBytes}.");
        if (length == 0)
            return new FrameReadResult.Dropped("Zero-length frame.");

        var buffer = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            try
            {
                await stream.ReadExactlyAsync(buffer.AsMemory(0, (int)length), ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return FrameReadResult.EndOfStream.Instance;
            }

            try
            {
                var msg = JsonSerializer.Deserialize<AgentMessage>(buffer.AsSpan(0, (int)length), AgentJson.Options);
                return msg is null
                    ? new FrameReadResult.Dropped("Frame was JSON null.")
                    : new FrameReadResult.Frame(msg);
            }
            catch (JsonException ex)
            {
                return new FrameReadResult.Dropped("Malformed frame: " + ex.GetType().Name);
            }
            catch (NotSupportedException ex)
            {
                return new FrameReadResult.Dropped("Unsupported frame: " + ex.GetType().Name);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

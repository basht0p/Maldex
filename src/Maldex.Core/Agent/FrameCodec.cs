using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace Maldex.Core.Agent;

internal enum FrameStatus { Ok, Dropped, Fatal, EndOfStream }

/// <summary>Direction-agnostic framing: 4-byte big-endian length prefix + UTF-8 JSON, shared by both directions.</summary>
internal static class FrameCodec
{
    public static async ValueTask<(FrameStatus Status, T? Value, string? Reason)> ReadAsync<T>(Stream stream, CancellationToken ct)
        where T : class
    {
        var header = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return (FrameStatus.EndOfStream, null, null);
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > AgentFrameReader.MaxFrameBytes)
            return (FrameStatus.Fatal, null, $"Frame length {length} exceeds maximum {AgentFrameReader.MaxFrameBytes}.");
        if (length == 0)
            return (FrameStatus.Dropped, null, "Zero-length frame.");

        var buffer = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            try
            {
                await stream.ReadExactlyAsync(buffer.AsMemory(0, (int)length), ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return (FrameStatus.EndOfStream, null, null);
            }

            try
            {
                var value = JsonSerializer.Deserialize<T>(buffer.AsSpan(0, (int)length), AgentJson.Options);
                return value is null
                    ? (FrameStatus.Dropped, null, "Frame was JSON null.")
                    : (FrameStatus.Ok, value, null);
            }
            catch (JsonException ex)
            {
                return (FrameStatus.Dropped, null, "Malformed frame: " + ex.GetType().Name);
            }
            catch (NotSupportedException ex)
            {
                return (FrameStatus.Dropped, null, "Unsupported frame: " + ex.GetType().Name);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask WriteAsync<T>(Stream stream, T value, string what, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, AgentJson.Options);
        if (payload.Length > AgentFrameReader.MaxFrameBytes)
            throw new InvalidOperationException($"{what} payload {payload.Length} bytes exceeds maximum {AgentFrameReader.MaxFrameBytes}.");

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}

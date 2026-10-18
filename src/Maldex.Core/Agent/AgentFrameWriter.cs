using System.Buffers.Binary;
using System.Text.Json;

namespace Maldex.Core.Agent;

public static class AgentFrameWriter
{
    public static async ValueTask WriteAsync(Stream stream, AgentCommand command, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes<AgentCommand>(command, AgentJson.Options);
        if (payload.Length > AgentFrameReader.MaxFrameBytes)
            throw new InvalidOperationException($"Command payload {payload.Length} bytes exceeds maximum {AgentFrameReader.MaxFrameBytes}.");

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}

using System.Security.Cryptography;
using Maldex.Core.Agent;

namespace Maldex.Agent.Transfers;

/// <summary>Reads one file (at most <see cref="AgentLimits.MaxDroppedFileBytes"/>) and sends it as chunks the host can verify.</summary>
public static class OutboundFileSender
{
    /// <summary>
    /// Sends <paramref name="path"/> as <see cref="FileChunkMessage"/>s through <paramref name="emit"/>. Throws
    /// <see cref="InvalidOperationException"/> before sending anything if the file is missing, not a regular file or too large.
    /// </summary>
    public static async Task SendAsync(string transferId, string path, Func<AgentMessage, CancellationToken, Task> emit, TimeProvider time, CancellationToken ct)
    {
        byte[] data;
        try
        {
            if (!File.Exists(path)) throw new InvalidOperationException("File not found.");
            // Dropped files are usually still open by the process that wrote them.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > AgentLimits.MaxDroppedFileBytes)
                throw new InvalidOperationException($"File is {stream.Length} bytes; the limit is {AgentLimits.MaxDroppedFileBytes}.");
            data = new byte[stream.Length];
            await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException("File could not be read: " + ex.Message);
        }

        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var count = Math.Max(1, (data.Length + AgentLimits.ChunkBytes - 1) / AgentLimits.ChunkBytes);
        for (var i = 0; i < count; i++)
        {
            var offset = i * AgentLimits.ChunkBytes;
            var part = data.AsMemory(offset, Math.Min(AgentLimits.ChunkBytes, data.Length - offset));
            var chunk = new FileChunkMessage(time.GetUtcNow(), transferId, i, count, data.Length, sha, path, Convert.ToBase64String(part.Span));
            await emit(chunk, ct).ConfigureAwait(false);
        }
    }
}

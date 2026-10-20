using System.Security.Cryptography;
using System.Text;
using Maldex.Core.Agent;

namespace Maldex.Agent.Transfers;

/// <summary>
/// Tracks sample deliveries by TransferId. Chunks are staged on disk and verified when the last one arrives; the sample only
/// reaches the Desktop when <c>open_sample</c> names a verified transfer. Any failure discards that transfer's data.
/// </summary>
public sealed class InboundTransfers(string stagingDirectory) : IDisposable
{
    private const int MaxOpenTransfers = 4;

    private readonly object _gate = new();
    private readonly Dictionary<string, InboundFileAssembler> _active = [];
    private readonly Dictionary<string, string> _verified = [];

    /// <summary>Accepts one chunk. Throws <see cref="InvalidOperationException"/> (and discards the transfer) on any violation.</summary>
    public void AddChunk(PutFileChunkCommand c)
    {
        lock (_gate)
        {
            try
            {
                AddChunkCore(c);
            }
            catch
            {
                Discard(c.TransferId);
                throw;
            }
        }
    }

    private void AddChunkCore(PutFileChunkCommand c)
    {
        if (string.IsNullOrEmpty(c.TransferId) || c.TransferId.Length > 256) throw new InvalidOperationException("Invalid TransferId.");
        if (c.Count is < 1 or > AgentLimits.MaxChunks) throw new InvalidOperationException($"Chunk count {c.Count} outside 1..{AgentLimits.MaxChunks}.");
        if (c.Index < 0 || c.Index >= c.Count) throw new InvalidOperationException($"Chunk index {c.Index} outside 0..{c.Count - 1}.");
        if (c.TotalBytes < 0 || c.TotalBytes > AgentLimits.MaxInboundBytes) throw new InvalidOperationException($"Total size {c.TotalBytes} exceeds {AgentLimits.MaxInboundBytes} bytes.");
        if (c.DataBase64 is null || c.DataBase64.Length > (AgentLimits.ChunkBytes + 2) / 3 * 4) throw new InvalidOperationException("Chunk data exceeds the chunk size limit.");

        var data = new byte[c.DataBase64.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64String(c.DataBase64, data, out var n)) throw new InvalidOperationException("Chunk data is not valid base64.");

        if (c.Index == 0)
        {
            // A fresh start for this id replaces any earlier attempt.
            Discard(c.TransferId);
            if (_active.Count >= MaxOpenTransfers) throw new InvalidOperationException("Too many transfers in progress.");
            _active[c.TransferId] = new InboundFileAssembler(stagingDirectory, StageName(c.TransferId), c.Sha256, c.Count, c.TotalBytes);
        }

        if (!_active.TryGetValue(c.TransferId, out var assembler)) throw new InvalidOperationException("Unknown transfer; chunk 0 must come first.");
        assembler.AddChunk(c.Index, data.AsSpan(0, n));

        if (c.Index == c.Count - 1)
        {
            _active.Remove(c.TransferId);
            try
            {
                _verified[c.TransferId] = assembler.Complete(); // deletes the data and throws on a hash mismatch
            }
            finally
            {
                assembler.Dispose();
            }
        }
    }

    /// <summary>Moves a verified transfer to <paramref name="directory"/> under a sanitised name and returns the final path.</summary>
    public string Publish(string transferId, string fileName, string directory)
    {
        var safe = InboundFileAssembler.SanitizeFileName(fileName);
        lock (_gate)
        {
            if (!_verified.Remove(transferId, out var staged)) throw new InvalidOperationException("Unknown or incomplete transfer.");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, safe);
            File.Move(staged, target, overwrite: true);
            return target;
        }
    }

    private void Discard(string transferId)
    {
        if (_active.Remove(transferId, out var a)) a.Dispose();
        if (_verified.Remove(transferId, out var path))
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string StageName(string transferId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(transferId)))[..32] + ".part";

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var a in _active.Values) a.Dispose();
            _active.Clear();
            foreach (var p in _verified.Values)
            {
                try { File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            _verified.Clear();
        }
    }
}

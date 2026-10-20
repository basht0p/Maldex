using System.Security.Cryptography;

namespace Maldex.Agent.Transfers;

/// <summary>
/// Reassembles a file delivered in sequential chunks (512 KiB raw each) and verifies its SHA-256 before
/// exposing it. The chunk commands themselves are wired by a later task.
/// </summary>
public sealed class InboundFileAssembler : IDisposable
{
    public const int ChunkBytes = 512 * 1024;

    private readonly FileStream _file;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly string _expectedSha256;
    private int _nextIndex;

    public string Path { get; }

    public InboundFileAssembler(string directory, string fileName, string expectedSha256Hex)
    {
        var safe = System.IO.Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
            throw new ArgumentException("Invalid file name.", nameof(fileName));
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, safe);
        _expectedSha256 = expectedSha256Hex;
        _file = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.None);
    }

    public void AddChunk(int index, ReadOnlySpan<byte> data)
    {
        if (index != _nextIndex)
            throw new InvalidOperationException($"Chunk {index} out of order; expected {_nextIndex}.");
        if (data.Length is 0 or > ChunkBytes)
            throw new InvalidOperationException($"Chunk size {data.Length} outside 1..{ChunkBytes}.");
        _file.Write(data);
        _hash.AppendData(data);
        _nextIndex++;
    }

    /// <summary>Closes the file and verifies the hash; deletes the file and throws on mismatch. Returns the path.</summary>
    public string Complete()
    {
        _file.Dispose();
        var actual = Convert.ToHexString(_hash.GetHashAndReset());
        if (!string.Equals(actual, _expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(Path);
            throw new InvalidOperationException("SHA-256 mismatch.");
        }
        return Path;
    }

    public void Dispose()
    {
        _file.Dispose();
        _hash.Dispose();
    }
}

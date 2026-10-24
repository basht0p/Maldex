using System.Security.Cryptography;
using Maldex.Core.Agent;

namespace Maldex.Agent.Transfers;

/// <summary>
/// Reassembles a file delivered in sequential chunks (at most <see cref="ChunkBytes"/> raw each) and verifies its SHA-256 before
/// exposing it. Limits: <see cref="MaxChunks"/> chunks and <see cref="MaxTotalBytes"/> bytes per file (component files, see
/// <see cref="AgentLimits.ComponentTransferPrefix"/>, get the larger component limits); a hash mismatch deletes the partial data. Everything the host sends is bounds-checked here even though the host is trusted, because this is the
/// last line of defence for the guest's disk.
/// </summary>
public sealed class InboundFileAssembler : IDisposable
{
    public const int ChunkBytes = AgentLimits.ChunkBytes;
    public const int MaxChunks = AgentLimits.MaxChunks;
    public const long MaxTotalBytes = AgentLimits.MaxInboundBytes;
    public const int MaxFileNameChars = 200;

    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    private readonly FileStream _file;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly int _maxChunks;
    private readonly long _maxTotalBytes;
    private readonly string _expectedSha256;
    private readonly int _expectedCount;
    private readonly long _expectedTotal;
    private int _nextIndex;
    private long _written;
    private bool _completed;

    public string Path { get; }

    /// <param name="expectedCount">Chunk count the sender declared (0 = not declared).</param>
    /// <param name="expectedTotal">Byte count the sender declared (-1 = not declared).</param>
    /// <param name="component">True for a Maldex component file, which may be larger than a sample.</param>
    public InboundFileAssembler(string directory, string fileName, string expectedSha256Hex, int expectedCount = 0, long expectedTotal = -1, bool component = false)
    {
        _maxChunks = component ? AgentLimits.MaxComponentChunks : MaxChunks;
        _maxTotalBytes = component ? AgentLimits.MaxComponentBytes : MaxTotalBytes;
        if (expectedCount < 0 || expectedCount > _maxChunks) throw new InvalidOperationException($"Chunk count {expectedCount} outside 1..{_maxChunks}.");
        if (expectedTotal < -1 || expectedTotal > _maxTotalBytes) throw new InvalidOperationException($"Total size {expectedTotal} exceeds {_maxTotalBytes} bytes.");
        var safe = SanitizeFileName(fileName);
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, safe);
        _expectedSha256 = expectedSha256Hex;
        _expectedCount = expectedCount;
        _expectedTotal = expectedTotal;
        _file = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.None);
    }

    /// <summary>
    /// The leaf of a guest-or-host supplied name with characters Windows forbids replaced by '_', trailing dots and spaces
    /// removed, reserved device names prefixed, and at most <see cref="MaxFileNameChars"/> characters (the extension is kept).
    /// Throws <see cref="ArgumentException"/> when nothing usable remains.
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        var leafStart = fileName.AsSpan().LastIndexOfAny('/', '\\') + 1;
        var leaf = fileName[leafStart..];
        var chars = leaf.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] < 0x20 || chars[i] == 0x7F || Array.IndexOf(InvalidNameChars, chars[i]) >= 0) chars[i] = '_';
        var name = new string(chars).TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name) || name.TrimStart('.').Length == 0)
            throw new ArgumentException("Invalid file name.", nameof(fileName));

        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var ext = dot > 0 ? name[dot..] : "";
        if (Array.Exists(ReservedNames, r => string.Equals(r, stem, StringComparison.OrdinalIgnoreCase))) name = "_" + name;

        if (name.Length > MaxFileNameChars)
        {
            ext = ext.Length is > 0 and <= 32 ? ext : "";
            name = name[..(MaxFileNameChars - ext.Length)] + ext;
        }
        return name;
    }

    public void AddChunk(int index, ReadOnlySpan<byte> data)
    {
        if (index != _nextIndex)
            throw new InvalidOperationException($"Chunk {index} out of order; expected {_nextIndex}.");
        if (data.Length is 0 or > ChunkBytes)
            throw new InvalidOperationException($"Chunk size {data.Length} outside 1..{ChunkBytes}.");
        if (_nextIndex >= (_expectedCount > 0 ? _expectedCount : _maxChunks))
            throw new InvalidOperationException("Too many chunks.");
        if (_written + data.Length > (_expectedTotal >= 0 ? _expectedTotal : _maxTotalBytes))
            throw new InvalidOperationException("Transfer exceeds its declared size.");
        _file.Write(data);
        _hash.AppendData(data);
        _written += data.Length;
        _nextIndex++;
    }

    /// <summary>Closes the file and verifies count, size and hash; deletes the file and throws on any mismatch. Returns the path.</summary>
    public string Complete()
    {
        _file.Dispose();
        _completed = true;
        var actual = Convert.ToHexString(_hash.GetHashAndReset());
        if (_expectedCount > 0 && _nextIndex != _expectedCount)
        {
            Discard();
            throw new InvalidOperationException($"Transfer incomplete: {_nextIndex} of {_expectedCount} chunks.");
        }
        if (_expectedTotal >= 0 && _written != _expectedTotal)
        {
            Discard();
            throw new InvalidOperationException($"Size mismatch: {_written} bytes, expected {_expectedTotal}.");
        }
        if (!string.Equals(actual, _expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            Discard();
            throw new InvalidOperationException("SHA-256 mismatch.");
        }
        return Path;
    }

    /// <summary>Closes and deletes whatever has been written.</summary>
    public void Discard()
    {
        _file.Dispose();
        try { File.Delete(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _file.Dispose();
        _hash.Dispose();
        // An abandoned transfer must not leave partial data behind.
        if (!_completed) Discard();
    }
}

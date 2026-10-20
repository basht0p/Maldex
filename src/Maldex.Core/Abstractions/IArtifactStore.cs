namespace Maldex.Core.Abstractions;

public sealed record StoredBlob(string Key, string Sha256, string Sha1, string Md5, long Size);

public sealed class BlobTooLargeException(long maxBytes)
    : Exception($"Content exceeds the maximum allowed size of {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}

public interface IArtifactStore
{
    /// <summary>Stores content under a key derived from its SHA-256. Throws <see cref="BlobTooLargeException"/> past maxBytes.</summary>
    Task<StoredBlob> PutAsync(string category, Stream content, long maxBytes, CancellationToken ct);

    Task<Stream> OpenReadAsync(string key, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);

    bool Exists(string key);

    /// <summary>Enumerates every stored blob with its last-write time (in-flight temp files are excluded).</summary>
    IAsyncEnumerable<(string Key, DateTimeOffset LastWrite)> ListAsync(CancellationToken ct);
}

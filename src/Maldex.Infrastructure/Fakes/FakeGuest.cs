using System.Collections.Concurrent;

namespace Maldex.Infrastructure.Fakes;

/// <summary>A file the fake guest can hand back for <c>collect_file</c>. The claims default to the truth; tests lie through them.</summary>
public sealed record FakeGuestFile(byte[] Data, string? ClaimedSha256 = null, long? ClaimedTotalBytes = null);

/// <summary>
/// What the replayed "guest" has on disk and how it behaves during bulk transfers, shared by every fake channel.
/// Empty by default: sample chunks are accepted and every <c>collect_file</c> is answered "file not found".
/// </summary>
public sealed class FakeGuest
{
    private readonly ConcurrentDictionary<string, FakeGuestFile> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, the chunk with this index is answered with a failure (the agent "refusing" the delivery).</summary>
    public int? RejectSampleChunkIndex { get; set; }

    /// <summary>When set, the chunk with this index is never answered (a guest that hangs mid-delivery).</summary>
    public int? SilentSampleChunkIndex { get; set; }

    public void AddFile(string path, byte[] data) => _files[path] = new FakeGuestFile(data);

    public void AddFile(string path, FakeGuestFile file) => _files[path] = file;

    public bool TryGetFile(string path, out FakeGuestFile file) => _files.TryGetValue(path, out file!);
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace Maldex.Orchestrator.Media;

/// <summary>A unit of work for the <see cref="MediaWorker"/>: complete a fully received upload, or (with a URL) run a fetch.</summary>
public sealed record MediaWork(Guid Id, Uri? FetchUrl, bool NameGiven = false);

/// <summary>
/// Process-wide state of uploads in progress: one lock per upload (chunks of one file never overlap), the running SHA-256 of
/// the bytes received so far (rebuilt from the staging file when absent, i.e. after a restart), the worker queue and the
/// cancellation of running fetches. None of it is authoritative: the database row and the staging file are.
/// </summary>
public sealed class MediaUploadState : IDisposable
{
    public sealed class Upload
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public IncrementalHash? Hash { get; set; }
        /// <summary>How many bytes <see cref="Hash"/> has taken.</summary>
        public long HashedBytes { get; set; }

        public void DropHash()
        {
            Hash?.Dispose();
            Hash = null;
            HashedBytes = 0;
        }
    }

    private readonly ConcurrentDictionary<Guid, Upload> _uploads = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _fetches = new();

    /// <summary>Held while a new upload's disk check and row are written, so two starts cannot both pass the check.</summary>
    public SemaphoreSlim StartGate { get; } = new(1, 1);

    public Channel<MediaWork> Queue { get; } = Channel.CreateUnbounded<MediaWork>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>How many uploads have an entry (lock and hash).</summary>
    public int TrackedUploads => _uploads.Count;

    public Upload Get(Guid id) => _uploads.GetOrAdd(id, _ => new Upload());

    /// <summary>Drops an upload's entry (its running hash too) when it has ended; a chunk still holding the lock finishes on its own copy.</summary>
    public void Remove(Guid id)
    {
        if (_uploads.TryRemove(id, out var upload)) upload.DropHash();
    }

    public void RegisterFetch(Guid id, CancellationTokenSource cts) => _fetches[id] = cts;

    public void UnregisterFetch(Guid id) => _fetches.TryRemove(id, out _);

    /// <summary>Cancels a running fetch; true when there was one.</summary>
    public bool CancelFetch(Guid id)
    {
        if (!_fetches.TryGetValue(id, out var cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    public void Dispose()
    {
        foreach (var upload in _uploads.Values) upload.DropHash();
    }
}

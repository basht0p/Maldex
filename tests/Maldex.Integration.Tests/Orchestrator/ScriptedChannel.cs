using System.Security.Cryptography;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Orchestrator;

namespace Maldex.Integration.Tests.Orchestrator;

/// <summary>
/// An <see cref="IAgentChannel"/> whose "guest" is a callback: every command sent is recorded and the messages the callback
/// returns are delivered to the run's <see cref="TransferInbox"/> in the background, as the read loop would.
/// </summary>
internal sealed class ScriptedChannel(TransferInbox inbox, Func<AgentCommand, IEnumerable<AgentMessage>> guest) : IAgentChannel
{
    private readonly object _gate = new();
    private readonly List<AgentCommand> _sent = [];

    public IReadOnlyList<AgentCommand> Sent
    {
        get { lock (_gate) return _sent.ToArray(); }
    }

    public int Violations => 0;

    public IAsyncEnumerable<AgentMessage> ReadAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task SendAsync(AgentCommand command, CancellationToken ct)
    {
        lock (_gate) _sent.Add(command);
        var replies = guest(command).ToList();
        _ = Task.Run(async () =>
        {
            foreach (var r in replies) await inbox.OfferAsync(r, CancellationToken.None);
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

/// <summary>Keeps blobs in memory under a key derived from their hash.</summary>
internal sealed class MemoryArtifactStore : IArtifactStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _blobs = [];

    public IReadOnlyDictionary<string, byte[]> Blobs
    {
        get { lock (_gate) return new Dictionary<string, byte[]>(_blobs); }
    }

    public async Task<StoredBlob> PutAsync(string category, Stream content, long maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        if (bytes.Length > maxBytes) throw new BlobTooLargeException(maxBytes);
        var sha = ScriptedChannel.Sha(bytes);
        var key = $"{category}/{sha[..2]}/{sha[2..4]}/{sha}";
        lock (_gate) _blobs[key] = bytes;
        return new StoredBlob(key, sha, Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(), bytes.Length);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<Stream>(new MemoryStream(_blobs[key], writable: false));
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        lock (_gate) _blobs.Remove(key);
        return Task.CompletedTask;
    }

    public bool Exists(string key)
    {
        lock (_gate) return _blobs.ContainsKey(key);
    }

    public async IAsyncEnumerable<(string Key, DateTimeOffset LastWrite)> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        foreach (var k in Blobs.Keys) yield return (k, DateTimeOffset.UtcNow);
    }
}

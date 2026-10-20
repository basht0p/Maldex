using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Guacamole;

/// <summary>
/// Issues short-lived, single-use tunnel tokens bound to one task and one user. Tokens live in memory only
/// (a restart invalidates them, which is fine for a 60-second credential).
/// </summary>
public sealed class GuacamoleTokenService(TimeProvider time, IOptions<GuacamoleOptions> options)
{
    private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);

    private readonly record struct Entry(Guid TaskId, Guid UserId, DateTimeOffset Expires);

    public int PendingCount => _tokens.Count;

    public string Issue(Guid taskId, Guid userId)
    {
        var now = time.GetUtcNow();
        foreach (var (key, entry) in _tokens)
            if (entry.Expires <= now) _tokens.TryRemove(key, out _);

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = new Entry(taskId, userId, now + options.Value.TokenLifetime);
        return token;
    }

    /// <summary>
    /// Consumes the token (single use, even when it turns out to be expired) and returns the task and the user it was
    /// issued to. The token is the tunnel's only credential, so it must stay unguessable and short-lived.
    /// </summary>
    public bool TryConsume(string? token, out Guid taskId, out Guid userId)
    {
        taskId = default;
        userId = default;
        if (string.IsNullOrEmpty(token) || !_tokens.TryRemove(token, out var entry)) return false;
        if (entry.Expires <= time.GetUtcNow()) return false;
        taskId = entry.TaskId;
        userId = entry.UserId;
        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

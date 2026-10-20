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

    /// <summary>Consumes the token even when it does not match the user, so a stolen token cannot be probed.</summary>
    public bool TryConsume(string? token, Guid userId, out Guid taskId)
    {
        taskId = default;
        if (string.IsNullOrEmpty(token) || !_tokens.TryRemove(token, out var entry)) return false;
        if (entry.Expires <= time.GetUtcNow() || entry.UserId != userId) return false;
        taskId = entry.TaskId;
        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

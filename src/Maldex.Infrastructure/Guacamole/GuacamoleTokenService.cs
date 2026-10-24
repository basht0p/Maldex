using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Guacamole;

/// <summary>What a tunnel token opens: the live view of an analysis task, or the configure console of a template job.</summary>
public enum TunnelTokenKind { Task, TemplateJob }

/// <summary>
/// Issues short-lived, single-use tunnel tokens bound to one task (or template job) and one user. Tokens live in memory only
/// (a restart invalidates them, which is fine for a 60-second credential).
/// </summary>
public sealed class GuacamoleTokenService(TimeProvider time, IOptions<GuacamoleOptions> options)
{
    private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);

    private readonly record struct Entry(TunnelTokenKind Kind, Guid TargetId, Guid UserId, DateTimeOffset Expires);

    public int PendingCount => _tokens.Count;

    public string Issue(Guid taskId, Guid userId) => Issue(TunnelTokenKind.Task, taskId, userId);

    public string Issue(TunnelTokenKind kind, Guid targetId, Guid userId)
    {
        var now = time.GetUtcNow();
        foreach (var (key, entry) in _tokens)
            if (entry.Expires <= now) _tokens.TryRemove(key, out _);

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = new Entry(kind, targetId, userId, now + options.Value.TokenLifetime);
        return token;
    }

    public bool TryConsume(string? token, out Guid taskId, out Guid userId) =>
        TryConsume(TunnelTokenKind.Task, token, out taskId, out userId);

    /// <summary>
    /// Consumes the token (single use, even when it turns out to be expired or of another kind) and returns the task or job and
    /// the user it was issued to. A token of the wrong kind opens nothing. The token is the tunnel's only credential, so it
    /// must stay unguessable and short-lived.
    /// </summary>
    public bool TryConsume(TunnelTokenKind kind, string? token, out Guid targetId, out Guid userId)
    {
        targetId = default;
        userId = default;
        if (string.IsNullOrEmpty(token) || !_tokens.TryRemove(token, out var entry)) return false;
        if (entry.Expires <= time.GetUtcNow() || entry.Kind != kind) return false;
        targetId = entry.TargetId;
        userId = entry.UserId;
        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

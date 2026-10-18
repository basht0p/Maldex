using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Web.Auth;

namespace Maldex.Web.Audit;

public sealed class AuditWriter(MaldexDbContext db, CurrentUser user, TimeProvider time)
{
    /// <summary>
    /// Adds an audit row and saves the context. Because the context is scoped to the request, any
    /// changes already tracked (for example a new org) are committed in the same transaction.
    /// </summary>
    public async Task WriteAsync(string action, string target, string? details, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            At = time.GetUtcNow(),
            UserId = user.IsResolved ? user.UserId : null,
            Action = action,
            Target = target,
            Details = details,
        });
        await db.SaveChangesAsync(ct);
    }
}

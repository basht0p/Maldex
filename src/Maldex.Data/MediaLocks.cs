using System.Text;
using Maldex.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Data;

/// <summary>Row locks on media files, so that deleting a file and queuing a Build that uses it cannot both succeed.</summary>
public static class MediaLocks
{
    /// <summary>
    /// Locks the given media rows (<c>UPDLOCK, HOLDLOCK</c>) until the caller's transaction ends and returns them tracked.
    /// The caller owns the transaction; without one the locks are released at once.
    /// </summary>
    public static async Task<List<MediaFile>> LockAsync(MaldexDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var distinct = ids.Distinct().ToList();
        var sql = new StringBuilder("SELECT * FROM MediaFiles WITH (UPDLOCK, HOLDLOCK) WHERE Id IN (");
        for (var i = 0; i < distinct.Count; i++) sql.Append(i == 0 ? "{" : ", {").Append(i).Append('}');
        sql.Append(')');
        return await db.MediaFiles.FromSqlRaw(sql.ToString(), distinct.Cast<object>().ToArray()).ToListAsync(ct).ConfigureAwait(false);
    }
}

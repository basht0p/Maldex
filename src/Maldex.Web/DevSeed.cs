using Maldex.Core.Domain;
using Maldex.Data;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Web;

public static class DevSeed
{
    /// <summary>Development only: creates "Dev Org" (retention 7 days) when no orgs exist.</summary>
    public static async Task SeedAsync(MaldexDbContext db, CancellationToken ct = default)
    {
        if (await db.Orgs.AnyAsync(ct)) return;
        db.Orgs.Add(new Org { Id = Guid.NewGuid(), Name = "Dev Org", RetentionDays = 7 });
        await db.SaveChangesAsync(ct);
    }
}

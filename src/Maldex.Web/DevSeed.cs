using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Web.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Maldex.Web;

public static class DevSeed
{
    public const string DevAdminUserName = "dev";
    public const string DevAdminPassword = "dev-password-123";

    /// <summary>
    /// Development only: creates "Dev Org" (retention 7 days) when no orgs exist and, with <c>Maldex:Auth:Mode=Local</c>, the local
    /// Admin <c>dev</c> / <c>dev-password-123</c> when nobody can sign in with a password yet (rows the Dev scheme provisioned
    /// have none). The account is logged as a warning.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var db = services.GetRequiredService<MaldexDbContext>();
        if (!await db.Orgs.AnyAsync(ct))
        {
            db.Orgs.Add(new Org { Id = Guid.NewGuid(), Name = "Dev Org", RetentionDays = 7 });
            await db.SaveChangesAsync(ct);
        }

        var auth = services.GetRequiredService<IOptions<MaldexAuthOptions>>().Value;
        if (auth.IsDev || await db.Users.AnyAsync(u => u.PasswordHash != null, ct)) return;
        var users = services.GetRequiredService<UserManager<AppUser>>();
        if (await users.FindByNameAsync(DevAdminUserName) is not null) return;

        var created = await users.CreateAsync(new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = DevAdminUserName,
            DisplayName = "Dev Admin",
            Role = UserRole.Admin,
            Kind = UserKind.Local,
            CreatedAt = services.GetRequiredService<TimeProvider>().GetUtcNow(),
        }, DevAdminPassword);
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Maldex.DevSeed");
        if (created.Succeeded)
            log.LogWarning("Development: created the local Admin '{UserName}' with password '{Password}'", DevAdminUserName, DevAdminPassword);
        else
            log.LogWarning("Development: could not create the local Admin '{UserName}': {Errors}", DevAdminUserName, string.Join("; ", created.Errors.Select(e => e.Code)));
    }
}

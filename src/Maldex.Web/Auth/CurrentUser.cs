using System.Security.Claims;
using Maldex.Core.Domain;
using Maldex.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Web.Auth;

/// <summary>The authenticated caller, resolved (and provisioned in the database) once per request.</summary>
public sealed class CurrentUser(MaldexDbContext db)
{
    private AppUser? _user;

    public bool IsResolved => _user is not null;
    public Guid UserId => _user?.Id ?? throw new InvalidOperationException("No current user");
    public UserRole Role => _user?.Role ?? throw new InvalidOperationException("No current user");
    public string DisplayName => _user?.DisplayName ?? throw new InvalidOperationException("No current user");

    /// <summary>Loads the user row by Entra object id, creating or updating it. No-op without a Maldex role.</summary>
    public async Task ResolveAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (principal.Identity?.IsAuthenticated != true || _user is not null) return;
        var role = AuthSetup.MapRole(principal);
        var oid = AuthSetup.ObjectId(principal);
        if (role is null || string.IsNullOrEmpty(oid)) return;

        var name = Truncate(principal.FindFirstValue(ClaimTypes.Name) ?? principal.FindFirstValue("name") ?? oid, 200);

        for (var attempt = 0; ; attempt++)
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.EntraObjectId == oid, ct);
            if (user is null)
            {
                user = new AppUser { Id = Guid.NewGuid(), EntraObjectId = oid, DisplayName = name, Role = role.Value };
                db.Users.Add(user);
            }
            else if (user.DisplayName == name && user.Role == role.Value)
            {
                _user = user;
                return;
            }
            else
            {
                user.DisplayName = name;
                user.Role = role.Value;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                _user = user;
                return;
            }
            catch (DbUpdateException ex) when (attempt == 0 && IsUniqueViolation(ex))
            {
                // Concurrent first request created the row; drop our insert and re-read.
                db.ChangeTracker.Clear();
            }
        }
    }

    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.GetBaseException() is SqlException { Number: 2627 or 2601 };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

public sealed class CurrentUserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, CurrentUser current)
    {
        await current.ResolveAsync(ctx.User, ctx.RequestAborted);
        await next(ctx);
    }
}

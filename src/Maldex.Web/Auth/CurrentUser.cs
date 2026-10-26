using System.Security.Claims;
using Maldex.Core.Domain;
using Maldex.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Web.Auth;

/// <summary>The authenticated caller, loaded from the Users table once per request.</summary>
public sealed class CurrentUser(MaldexDbContext db)
{
    private AppUser? _user;

    public bool IsResolved => _user is not null;
    public AppUser? User => _user;
    public Guid UserId => _user?.Id ?? throw new InvalidOperationException("No current user");
    public string DisplayName => _user?.DisplayName ?? throw new InvalidOperationException("No current user");

    /// <summary>The user's Maldex role; null when unresolved or when the account holds none.</summary>
    public UserRole? Role => _user?.Role;
    public bool HasRole => _user?.Role is not null;

    /// <summary>
    /// Loads the user row for the principal. The Dev scheme provisions or updates a row keyed by user name;
    /// any other scheme loads by <see cref="ClaimTypes.NameIdentifier"/> (a Guid). A missing or disabled row leaves the caller unresolved.
    /// </summary>
    public async Task ResolveAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (principal.Identity?.IsAuthenticated != true || _user is not null) return;

        if (principal.Identity.AuthenticationType == DevAuthHandler.SchemeName)
        {
            await ResolveDevAsync(principal, ct);
            return;
        }

        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null || user.IsDisabled) return;
        _user = user;
    }

    private async Task ResolveDevAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var name = principal.FindFirstValue(ClaimTypes.Name);
        var role = Enum.TryParse<UserRole>(principal.FindFirstValue(AuthSetup.DevRoleClaim), true, out var r) && Enum.IsDefined(r) ? r : (UserRole?)null;
        if (string.IsNullOrEmpty(name) || role is null) return;

        var userName = DevAuthHandler.DevUserName(name);
        var normalized = userName.ToUpperInvariant();
        var displayName = Truncate(name, 200);

        for (var attempt = 0; ; attempt++)
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized, ct);
            if (user is null)
            {
                user = new AppUser
                {
                    Id = Guid.NewGuid(), UserName = userName, NormalizedUserName = normalized, SecurityStamp = Guid.NewGuid().ToString("N"),
                    DisplayName = displayName, Role = role, Kind = UserKind.Local, CreatedAt = DateTimeOffset.UtcNow,
                };
                db.Users.Add(user);
            }
            else if (user.IsDisabled)
            {
                return;
            }
            else if (user.DisplayName == displayName && user.Role == role)
            {
                _user = user;
                return;
            }
            else
            {
                user.DisplayName = displayName;
                user.Role = role;
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
        // A valid cookie (or dev identity) whose user row is gone or disabled counts as not signed in: 401 on this very
        // request, without waiting for the next security-stamp check.
        if (ctx.User.Identity?.IsAuthenticated == true && !current.IsResolved) ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
        await next(ctx);
    }
}

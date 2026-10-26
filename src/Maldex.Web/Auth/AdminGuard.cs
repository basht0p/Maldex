using Maldex.Core.Domain;
using Maldex.Data;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Web.Auth;

/// <summary>Who can sign in, and the rules that keep at least one Admin able to (shared by the API and the admin CLI).</summary>
public static class AdminGuard
{
    public const string BreakGlassWarning =
        "No enabled break-glass admin: if single sign-on fails, nobody can sign in. Create one with: sudo maldex-admin create-user <name> --admin --break-glass";

    /// <summary>
    /// Not disabled, and either a local user allowed by <see cref="MaldexAuthOptions.PasswordLogin"/> (or break-glass), or an SSO
    /// user while SSO is enabled.
    /// </summary>
    public static bool CanSignIn(AppUser u, MaldexAuthOptions o) =>
        !u.IsDisabled
        && ((u.Kind == UserKind.Local && (o.PasswordLogin || u.IsBreakGlass))
            || (u.Kind == UserKind.Sso && o.Oidc.Enabled));

    /// <summary>
    /// True if, with <paramref name="after"/> standing in for the stored row of <paramref name="changedUserId"/>, at least one user
    /// is an Admin who can sign in. Only the Role, Kind, IsDisabled and IsBreakGlass of <paramref name="after"/> are read.
    /// </summary>
    public static async Task<bool> LeavesAnAdminAsync(MaldexDbContext db, MaldexAuthOptions o, Guid changedUserId, AppUser after, CancellationToken ct)
    {
        if (after.Role == UserRole.Admin && CanSignIn(after, o)) return true;
        var others = await db.Users.AsNoTracking()
            .Where(u => u.Id != changedUserId && u.Role == UserRole.Admin && !u.IsDisabled)
            .ToListAsync(ct);
        return others.Any(u => CanSignIn(u, o));
    }

    /// <summary>
    /// The sign-in warnings for System status: with password login off and SSO on, an IdP outage locks everybody out unless an
    /// enabled break-glass admin exists.
    /// </summary>
    public static async Task<IReadOnlyList<string>> BreakGlassWarnings(MaldexDbContext db, MaldexAuthOptions o, CancellationToken ct)
    {
        if (o.PasswordLogin || !o.Oidc.Enabled) return [];
        var any = await db.Users.AsNoTracking()
            .AnyAsync(u => u.IsBreakGlass && !u.IsDisabled && u.Kind == UserKind.Local && u.Role == UserRole.Admin, ct);
        return any ? [] : [BreakGlassWarning];
    }
}

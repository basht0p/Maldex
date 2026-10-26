using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Web.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Web.Auth;

/// <summary>Completes an OIDC callback (spec §6): find or provision the SSO user, map the role, refuse or issue the Maldex session.</summary>
public static class SsoSignIn
{
    public const string NoRoleTarget = "/login?error=no_role";
    public const string DisabledTarget = "/login?error=disabled";
    public const int MaxDisplayNameLength = 200;

    /// <summary>
    /// Handles the validated IdP identity <paramref name="external"/> and returns where to send the browser: the local
    /// <paramref name="returnUrl"/> (else <c>/</c>) after issuing the session cookie, or <c>/login?error=no_role</c> /
    /// <c>/login?error=disabled</c> after a refusal (no cookie).
    /// <list type="number">
    /// <item>The user is found by its login (<c>oidc:</c> + <c>iss</c>, <c>sub</c>) only, never by e-mail or user name; a first
    /// sign-in creates an SSO user (<c>sso-</c> + hex of SHA-256(iss|sub), no password) and its login in one save. A concurrent first
    /// sign-in that loses the insert race re-reads the winner's row.</item>
    /// <item><c>DisplayName</c> comes from <see cref="OidcOptions.NameClaim"/>, then <c>name</c>, <c>preferred_username</c>,
    /// <c>email</c>, <c>sub</c> (at most 200 characters).</item>
    /// <item>With <see cref="OidcOptions.RoleClaim"/> set, the role is Admin when any value is in <see cref="OidcOptions.AdminValues"/>,
    /// else Analyst when any is in <see cref="OidcOptions.AnalystValues"/>, else none (refused, but the row is kept and listed).
    /// Without it a new user has no role and an existing user keeps theirs.</item>
    /// <item>A disabled user is refused.</item>
    /// </list>
    /// Audit: <c>auth.sso_login</c>, or <c>auth.sso_refused</c> with Details <c>no_role</c> or <c>disabled</c>.
    /// </summary>
    public static async Task<string> CompleteAsync(HttpContext ctx, ClaimsPrincipal external, OidcOptions o, string? returnUrl = null)
    {
        var ct = ctx.RequestAborted;
        var services = ctx.RequestServices;
        var db = services.GetRequiredService<MaldexDbContext>();
        var audit = services.GetRequiredService<AuditWriter>();
        var time = services.GetRequiredService<TimeProvider>();

        var sub = external.FindFirst("sub") ?? throw new InvalidOperationException("The IdP's id_token has no sub claim");
        var issuer = external.FindFirstValue("iss") is { Length: > 0 } iss ? iss : sub.Issuer;
        var provider = "oidc:" + issuer;
        var displayName = DisplayNameOf(external, o, sub.Value);

        var user = await FindOrCreateAsync(db, provider, sub.Value, issuer, displayName, time.GetUtcNow(), ct);

        // Refresh what the IdP owns: the display name, and the role while a mapping is configured.
        user.DisplayName = displayName;
        if (!string.IsNullOrWhiteSpace(o.RoleClaim))
        {
            var role = MapRole(external, o);
            if (role != user.Role)
            {
                user.Role = role;
                // As an admin role change does: other sessions of this user end at their next stamp check.
                user.SecurityStamp = Guid.NewGuid().ToString("N");
            }
        }
        await db.SaveChangesAsync(ct);

        string? refusal = null;
        if (user.IsDisabled) refusal = "disabled";
        else if (!string.IsNullOrWhiteSpace(o.RoleClaim) && user.Role is null) refusal = "no_role";
        if (refusal is not null)
        {
            await audit.WriteAsync("auth.sso_refused", user.UserName ?? "", refusal, user.Id, ct);
            return refusal == "disabled" ? DisabledTarget : NoRoleTarget;
        }

        await LoginFlow.SignInFullAsync(ctx, user);
        await audit.WriteAsync("auth.sso_login", user.UserName ?? "", provider, user.Id, ct);
        return IsLocalPath(returnUrl) ? returnUrl! : "/";
    }

    /// <summary>True for a path on this site: starts with <c>/</c>, but not <c>//</c> or <c>/\</c> (both mean another host).</summary>
    public static bool IsLocalPath(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && !(url.Length > 1 && (url[1] == '/' || url[1] == '\\'))
        && !url.Any(char.IsControl);

    private static async Task<AppUser> FindOrCreateAsync(MaldexDbContext db, string provider, string sub, string issuer, string displayName,
        DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var existing = await FindByLoginAsync(db, provider, sub, ct);
            if (existing is not null) return existing;

            var userName = await FreeUserNameAsync(db, issuer, sub, ct);
            var user = new AppUser
            {
                Id = Guid.NewGuid(),
                UserName = userName,
                NormalizedUserName = userName.ToUpperInvariant(),
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
                DisplayName = displayName,
                Role = null,
                Kind = UserKind.Sso,
                LockoutEnabled = true,
                CreatedAt = now,
            };
            db.Users.Add(user);
            db.UserLogins.Add(new IdentityUserLogin<Guid> { LoginProvider = provider, ProviderKey = sub, ProviderDisplayName = "SSO", UserId = user.Id });
            try
            {
                await db.SaveChangesAsync(ct);
                return user;
            }
            catch (DbUpdateException ex) when (attempt == 0 && CurrentUser.IsUniqueViolation(ex))
            {
                // A concurrent first sign-in for the same (iss, sub) inserted first: drop our rows and re-read once.
                db.ChangeTracker.Clear();
            }
        }
    }

    private static async Task<AppUser?> FindByLoginAsync(MaldexDbContext db, string provider, string sub, CancellationToken ct)
    {
        var userId = await db.UserLogins.Where(l => l.LoginProvider == provider && l.ProviderKey == sub).Select(l => (Guid?)l.UserId).FirstOrDefaultAsync(ct);
        return userId is { } id ? await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) : null;
    }

    /// <summary><c>sso-</c> + the first 8 hex characters of SHA-256(iss|sub), extended to 12 then 16 on a collision.</summary>
    private static async Task<string> FreeUserNameAsync(MaldexDbContext db, string issuer, string sub, CancellationToken ct)
    {
        var hex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(issuer + "|" + sub)));
        foreach (var length in new[] { 8, 12, 16 })
        {
            var candidate = "sso-" + hex[..length];
            var normalized = candidate.ToUpperInvariant();
            if (!await db.Users.AnyAsync(u => u.NormalizedUserName == normalized, ct)) return candidate;
        }
        throw new InvalidOperationException($"No free SSO user name for {issuer} | {sub}");
    }

    private static string DisplayNameOf(ClaimsPrincipal external, OidcOptions o, string sub)
    {
        string[] claims = [o.NameClaim, "name", "preferred_username", "email"];
        var name = claims.Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => external.FindFirstValue(c)?.Trim())
            .FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? sub;
        return name.Length <= MaxDisplayNameLength ? name : name[..MaxDisplayNameLength];
    }

    private static UserRole? MapRole(ClaimsPrincipal external, OidcOptions o)
    {
        var values = external.FindAll(o.RoleClaim).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        if (o.AdminValues.Any(values.Contains)) return UserRole.Admin;
        if (o.AnalystValues.Any(values.Contains)) return UserRole.Analyst;
        return null;
    }
}

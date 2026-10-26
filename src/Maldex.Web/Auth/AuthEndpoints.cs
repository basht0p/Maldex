using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Web.Audit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Maldex.Web.Auth;

public sealed record LoginRequest(string? UserName, string? Password);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary><c>/api/auth</c>: sign-in options, password sign-in, password change and sign-out.</summary>
public static class AuthEndpoints
{
    /// <summary>A hash of a random password, verified against when the user does not exist, so both failures cost the same.</summary>
    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<AppUser>().HashPassword(new AppUser(), Guid.NewGuid().ToString("N")));

    public static void Map(IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").AllowAnonymous();

        auth.MapGet("/config", (IOptions<MaldexAuthOptions> options) =>
        {
            var o = options.Value;
            return Results.Ok(new { passwordLogin = o.PasswordLogin, sso = o.Oidc.Enabled ? new { displayName = o.Oidc.DisplayName } : null });
        });

        auth.MapPost("/login", LoginAsync).RequireRateLimiting(LocalAuthSetup.LoginRateLimitPolicy);
        auth.MapPost("/password", ChangePasswordAsync);
        auth.MapPost("/logout", LogoutAsync);
    }

    internal static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);

    /// <summary>The single-row <see cref="SystemSettings"/>; defaults when the row is missing.</summary>
    internal static async Task<SystemSettings> SettingsAsync(MaldexDbContext db, CancellationToken ct) =>
        await db.SystemSettings.FindAsync([1], ct) ?? new SystemSettings();

    private static async Task<IResult> LoginAsync(LoginRequest req, HttpContext ctx, UserManager<AppUser> users, MaldexDbContext db,
        IOptions<MaldexAuthOptions> options, AuditWriter audit, CancellationToken ct)
    {
        var o = options.Value;
        // User names are trimmed and compared case-insensitively (Identity normalizes them); passwords are used exactly as sent.
        var userName = (req.UserName ?? "").Trim();
        var password = req.Password ?? "";
        var target = userName.Length <= 128 ? userName : userName[..128];

        var user = userName.Length is > 0 and <= LocalAuthSetup.MaxUserNameLength && password.Length > 0 ? await users.FindByNameAsync(userName) : null;
        if (user is null || user.PasswordHash is null)
        {
            users.PasswordHasher.VerifyHashedPassword(new AppUser(), DummyHash.Value, password);
            await audit.WriteAsync("auth.login_failed", target, "invalid", user?.Id, ct);
            return Error(StatusCodes.Status401Unauthorized, "invalid");
        }

        // Lockout applies to every local account, also rows created outside UserManager.
        if (!await users.GetLockoutEnabledAsync(user)) await users.SetLockoutEnabledAsync(user, true);
        // Ruling T2-1: a locked account is answered "locked" without checking the password against its hash (the dummy
        // verify keeps the timing), and the attempt is not counted.
        if (await users.IsLockedOutAsync(user))
        {
            users.PasswordHasher.VerifyHashedPassword(new AppUser(), DummyHash.Value, password);
            await audit.WriteAsync("auth.login_failed", target, "locked", user.Id, ct);
            return Error(StatusCodes.Status401Unauthorized, "locked");
        }
        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            if (await users.IsLockedOutAsync(user)) await audit.WriteAsync("auth.lockout", target, null, user.Id, ct);
            await audit.WriteAsync("auth.login_failed", target, "invalid", user.Id, ct);
            return Error(StatusCodes.Status401Unauthorized, "invalid");
        }

        // Only now, with the password verified, may the answer say more than "invalid".
        string? refusal = null;
        if (user.IsDisabled) refusal = "disabled";
        else if (!o.PasswordLogin && !(user.Kind == UserKind.Local && user.IsBreakGlass && user.Role == UserRole.Admin)) refusal = "password_login_disabled";
        if (refusal is not null)
        {
            await audit.WriteAsync("auth.login_failed", target, refusal, user.Id, ct);
            return Error(StatusCodes.Status401Unauthorized, refusal);
        }

        await users.ResetAccessFailedCountAsync(user);
        var next = await LoginFlow.NextAsync(user, o, await SettingsAsync(db, ct), users);
        if (next == LoginSteps.Done) await LoginFlow.SignInFullAsync(ctx, user);
        else await LoginFlow.SignInPartialAsync(ctx, user, next);
        await audit.WriteAsync("auth.login", user.UserName ?? target, "next=" + next, user.Id, ct);
        return Results.Ok(new { next });
    }

    private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest req, HttpContext ctx, UserManager<AppUser> users, MaldexDbContext db,
        IOptions<MaldexAuthOptions> options, AuditWriter audit, CancellationToken ct)
    {
        // A full session, or a partial one whose pending step is exactly the password change (never one still owing TOTP).
        if (await LoginFlow.PartialUserAsync(ctx) is not ({ } user, var step)
            || step is not (LoginSteps.Done or LoginSteps.ChangePassword)
            || user.Kind != UserKind.Local)
            return Error(StatusCodes.Status401Unauthorized, "unauthenticated");

        var current = req.CurrentPassword ?? "";
        var replacement = req.NewPassword ?? "";
        if (await users.IsLockedOutAsync(user)) return Error(StatusCodes.Status400BadRequest, "locked");
        if (current.Length == 0 || !await users.CheckPasswordAsync(user, current))
        {
            // A session must not become a way around the lockout.
            if (!await users.GetLockoutEnabledAsync(user)) await users.SetLockoutEnabledAsync(user, true);
            await users.AccessFailedAsync(user);
            if (await users.IsLockedOutAsync(user)) await audit.WriteAsync("auth.lockout", user.UserName ?? "", "password change", user.Id, ct);
            return Error(StatusCodes.Status400BadRequest, "invalid");
        }

        // Validates the new password and refreshes the security stamp (other sessions end at their next stamp check).
        var changed = await users.ChangePasswordAsync(user, current, replacement);
        if (!changed.Succeeded) return Error(StatusCodes.Status400BadRequest, PasswordErrorCode(changed));
        user.MustChangePassword = false;
        await users.UpdateAsync(user);
        await users.ResetAccessFailedCountAsync(user);
        await audit.WriteAsync("auth.password_changed", user.UserName ?? "", null, user.Id, ct);

        var next = await LoginFlow.NextAsync(user, options.Value, await SettingsAsync(db, ct), users, totpVerified: true);
        // From an existing session the re-issued cookie keeps its sign-in time (the 7-day cap does not restart); a partial
        // session finishing its password step is a new sign-in.
        if (next == LoginSteps.Done) await LoginFlow.SignInFullAsync(ctx, user, step == LoginSteps.Done ? LoginFlow.SessionIssuedAt(ctx) : null);
        else await LoginFlow.SignInPartialAsync(ctx, user, next);
        return Results.Ok(new { next });
    }

    /// <summary>The API error code for a refused new password: <c>password_too_short</c>, <c>password_too_long</c> or <c>invalid_password</c>.</summary>
    internal static string PasswordErrorCode(IdentityResult result)
    {
        var codes = result.Errors.Select(e => e.Code).ToList();
        if (codes.Contains(nameof(IdentityErrorDescriber.PasswordTooShort))) return "password_too_short";
        if (codes.Contains(PasswordMaxLengthValidator.Code)) return "password_too_long";
        if (codes.Contains(nameof(IdentityErrorDescriber.PasswordMismatch))) return "invalid";
        return "invalid_password";
    }

    private static async Task<IResult> LogoutAsync(HttpContext ctx, AuditWriter audit, CancellationToken ct)
    {
        var who = await LoginFlow.PartialUserAsync(ctx);
        await ctx.SignOutAsync(AuthSchemes.Application);
        await ctx.SignOutAsync(AuthSchemes.Partial);
        if (who is ({ } user, _)) await audit.WriteAsync("auth.logout", user.UserName ?? "", null, user.Id, ct);
        return Results.NoContent();
    }
}

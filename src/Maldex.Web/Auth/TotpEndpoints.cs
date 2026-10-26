using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Web.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Maldex.Web.Auth;

public sealed record TotpCodeRequest(string? Code);
public sealed record PasswordRequest(string? Password);

/// <summary>TOTP sign-in, enrolment and recovery codes under <c>/api/auth</c>.</summary>
public static class TotpEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").AllowAnonymous();
        auth.MapPost("/login/totp", LoginTotpAsync).RequireRateLimiting(LocalAuthSetup.LoginRateLimitPolicy);
        auth.MapPost("/totp/setup", SetupAsync);
        auth.MapPost("/totp/confirm", ConfirmAsync);
        auth.MapDelete("/totp", RemoveAsync);
        auth.MapPost("/totp/recovery-codes", RegenerateAsync);
    }

    private static IResult Unauthenticated() => AuthEndpoints.Error(StatusCodes.Status401Unauthorized, "unauthenticated");

    /// <summary>Re-issues the cookie that was in use after the security stamp changed (a full session keeps its sign-in time).</summary>
    private static Task ReissueAsync(HttpContext ctx, AppUser user, string step) =>
        step == LoginSteps.Done
            ? LoginFlow.SignInFullAsync(ctx, user, LoginFlow.SessionIssuedAt(ctx))
            : LoginFlow.SignInPartialAsync(ctx, user, step);

    /// <summary>The 6-digit code typed by the user, or a recovery code, with spaces and dashes removed.</summary>
    private static string Clean(string? code) => (code ?? "").Replace(" ", "").Replace("-", "").Trim();

    private static async Task<IResult> LoginTotpAsync(TotpCodeRequest req, HttpContext ctx, UserManager<AppUser> users, MaldexDbContext db,
        IOptions<MaldexAuthOptions> options, AuditWriter audit, CancellationToken ct)
    {
        if (await LoginFlow.PartialUserAsync(ctx) is not ({ } user, LoginSteps.Totp)) return Unauthenticated();
        var name = user.UserName ?? "";

        if (await users.IsLockedOutAsync(user)) return AuthEndpoints.Error(StatusCodes.Status401Unauthorized, "locked");

        var code = Clean(req.Code);
        var valid = code.Length > 0
            && (code.Length == 6
                ? await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code)
                : (await users.RedeemTwoFactorRecoveryCodeAsync(user, MaldexUserStore.NormalizeCode(code))).Succeeded);
        if (!valid)
        {
            if (!await users.GetLockoutEnabledAsync(user)) await users.SetLockoutEnabledAsync(user, true);
            await users.AccessFailedAsync(user);
            if (await users.IsLockedOutAsync(user)) await audit.WriteAsync("auth.lockout", name, "totp", user.Id, ct);
            await audit.WriteAsync("auth.login_failed", name, "totp", user.Id, ct);
            return AuthEndpoints.Error(StatusCodes.Status401Unauthorized, "invalid");
        }

        await users.ResetAccessFailedCountAsync(user);
        if (code.Length != 6) await audit.WriteAsync("auth.recovery_codes", name, "recovery code used", user.Id, ct);
        var next = await LoginFlow.NextAsync(user, options.Value, await AuthEndpoints.SettingsAsync(db, ct), users, totpVerified: true);
        if (next == LoginSteps.Done) await LoginFlow.SignInFullAsync(ctx, user);
        else await LoginFlow.SignInPartialAsync(ctx, user, next);
        await audit.WriteAsync("auth.login", name, "totp next=" + next, user.Id, ct);
        return Results.Ok(new { next });
    }

    /// <summary>The local user of a full session or of a partial one whose pending step is the TOTP enrolment.</summary>
    private static async Task<(AppUser user, string step)?> EnrolUserAsync(HttpContext ctx)
    {
        if (await LoginFlow.PartialUserAsync(ctx) is not ({ } user, var step)
            || step is not (LoginSteps.Done or LoginSteps.EnrolTotp)
            || user.Kind != UserKind.Local)
            return null;
        return (user, step);
    }

    private static async Task<IResult> SetupAsync(HttpContext ctx, UserManager<AppUser> users)
    {
        if (await EnrolUserAsync(ctx) is not ({ } user, var step)) return Unauthenticated();
        // Replacing a working authenticator needs the removal flow (which asks for the password).
        if (await users.GetTwoFactorEnabledAsync(user)) return AuthEndpoints.Error(StatusCodes.Status409Conflict, "totp_enabled");

        var reset = await users.ResetAuthenticatorKeyAsync(user);
        if (!reset.Succeeded) return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "invalid");
        var key = await users.GetAuthenticatorKeyAsync(user) ?? "";
        // Resetting the key refreshes the security stamp; keep this sign-in alive.
        await ReissueAsync(ctx, user, step);
        var uri = $"otpauth://totp/Maldex:{Uri.EscapeDataString(user.UserName ?? "")}?secret={key}&issuer=Maldex&digits=6&period=30";
        return Results.Ok(new { sharedKey = key, otpauthUri = uri });
    }

    private static async Task<IResult> ConfirmAsync(TotpCodeRequest req, HttpContext ctx, UserManager<AppUser> users, MaldexDbContext db,
        IOptions<MaldexAuthOptions> options, AuditWriter audit, CancellationToken ct)
    {
        if (await EnrolUserAsync(ctx) is not ({ } user, var step)) return Unauthenticated();
        if (await users.GetTwoFactorEnabledAsync(user)) return AuthEndpoints.Error(StatusCodes.Status409Conflict, "totp_enabled");

        var code = Clean(req.Code);
        if (code.Length != 6 || !await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code))
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "invalid");

        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = await ReplaceRecoveryCodesAsync(ctx, users, user);
        // Other sessions end at their next stamp check, except while signing in (the pending cookie is the only one).
        if (step == LoginSteps.Done) await users.UpdateSecurityStampAsync(user);
        await audit.WriteAsync("auth.totp_enabled", user.UserName ?? "", null, user.Id, ct);

        var next = await LoginFlow.NextAsync(user, options.Value, await AuthEndpoints.SettingsAsync(db, ct), users, totpVerified: true);
        if (step == LoginSteps.Done) await LoginFlow.SignInFullAsync(ctx, user, LoginFlow.SessionIssuedAt(ctx));
        else if (next == LoginSteps.Done) await LoginFlow.SignInFullAsync(ctx, user);
        else await LoginFlow.SignInPartialAsync(ctx, user, next);
        return Results.Ok(new { recoveryCodes = codes, next = step == LoginSteps.Done ? LoginSteps.Done : next });
    }

    private static async Task<IResult> RemoveAsync([Microsoft.AspNetCore.Mvc.FromBody] PasswordRequest req, HttpContext ctx, UserManager<AppUser> users, MaldexDbContext db,
        IOptions<MaldexAuthOptions> options, AuditWriter audit, CancellationToken ct)
    {
        if (await FullSessionUserAsync(ctx) is not { } user) return Unauthenticated();

        var settings = await AuthEndpoints.SettingsAsync(db, ct);
        if (settings.RequireTotpForLocal || (!options.Value.PasswordLogin && user.IsBreakGlass))
            return AuthEndpoints.Error(StatusCodes.Status409Conflict, "totp_required");
        if (await CheckPasswordAsync(users, user, req.Password, audit, ct) is { } refusal) return refusal;

        await users.SetTwoFactorEnabledAsync(user, false);
        await users.RemoveAuthenticationTokenAsync(user, MaldexUserStore.InternalLoginProvider, MaldexUserStore.AuthenticatorKeyTokenName);
        await users.RemoveAuthenticationTokenAsync(user, MaldexUserStore.InternalLoginProvider, MaldexUserStore.RecoveryCodesTokenName);
        await users.UpdateSecurityStampAsync(user);
        await audit.WriteAsync("auth.totp_disabled", user.UserName ?? "", null, user.Id, ct);
        await LoginFlow.SignInFullAsync(ctx, user, LoginFlow.SessionIssuedAt(ctx));
        return Results.NoContent();
    }

    private static async Task<IResult> RegenerateAsync(PasswordRequest req, HttpContext ctx, UserManager<AppUser> users, AuditWriter audit, CancellationToken ct)
    {
        if (await FullSessionUserAsync(ctx) is not { } user) return Unauthenticated();
        if (!await users.GetTwoFactorEnabledAsync(user)) return AuthEndpoints.Error(StatusCodes.Status409Conflict, "totp_not_enabled");
        if (await CheckPasswordAsync(users, user, req.Password, audit, ct) is { } refusal) return refusal;

        var codes = await ReplaceRecoveryCodesAsync(ctx, users, user);
        await users.UpdateSecurityStampAsync(user);
        await audit.WriteAsync("auth.recovery_codes", user.UserName ?? "", "regenerated", user.Id, ct);
        await LoginFlow.SignInFullAsync(ctx, user, LoginFlow.SessionIssuedAt(ctx));
        return Results.Ok(new { recoveryCodes = codes });
    }

    /// <summary>The local user of a full session (never a partial one).</summary>
    private static async Task<AppUser?> FullSessionUserAsync(HttpContext ctx) =>
        await LoginFlow.PartialUserAsync(ctx) is ({ Kind: UserKind.Local } user, LoginSteps.Done) ? user : null;

    /// <summary>Null when the password is right; otherwise the refusal. A wrong password counts toward the lockout.</summary>
    private static async Task<IResult?> CheckPasswordAsync(UserManager<AppUser> users, AppUser user, string? password, AuditWriter audit, CancellationToken ct)
    {
        if (await users.IsLockedOutAsync(user)) return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "locked");
        if (!string.IsNullOrEmpty(password) && await users.CheckPasswordAsync(user, password)) return null;
        if (!await users.GetLockoutEnabledAsync(user)) await users.SetLockoutEnabledAsync(user, true);
        await users.AccessFailedAsync(user);
        if (await users.IsLockedOutAsync(user)) await audit.WriteAsync("auth.lockout", user.UserName ?? "", "totp settings", user.Id, ct);
        return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "invalid");
    }

    /// <summary>Stores a fresh set of hashed recovery codes and returns them in the clear, for showing once.</summary>
    private static async Task<string[]> ReplaceRecoveryCodesAsync(HttpContext ctx, UserManager<AppUser> users, AppUser user)
    {
        var store = (IUserTwoFactorRecoveryCodeStore<AppUser>)ctx.RequestServices.GetRequiredService<IUserStore<AppUser>>();
        var codes = MaldexUserStore.NewRecoveryCodes();
        await store.ReplaceCodesAsync(user, codes, ctx.RequestAborted);
        await users.UpdateAsync(user);
        return codes;
    }
}

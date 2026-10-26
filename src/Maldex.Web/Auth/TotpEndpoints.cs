using System.Security.Claims;
using System.Security.Cryptography;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Web.Audit;
using Microsoft.AspNetCore.Authentication;
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

    /// <summary>
    /// Re-issues the cookie that was in use after the security stamp changed. A full session keeps its sign-in time; a partial
    /// one keeps its original 10-minute window (enrolling must not extend it).
    /// </summary>
    private static async Task ReissueAsync(HttpContext ctx, AppUser user, string step)
    {
        if (step == LoginSteps.Done)
        {
            await LoginFlow.SignInFullAsync(ctx, user, LoginFlow.SessionIssuedAt(ctx));
            return;
        }
        var services = ctx.RequestServices;
        var window = (await ctx.AuthenticateAsync(AuthSchemes.Partial)).Properties;
        if (window?.IssuedUtc is null || window.ExpiresUtc is null)
        {
            await LoginFlow.SignInPartialAsync(ctx, user, step);
            return;
        }
        var stampType = services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
        var identity = new ClaimsIdentity(AuthSchemes.Partial);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(MaldexClaims.Next, step));
        identity.AddClaim(new Claim(stampType, await services.GetRequiredService<UserManager<AppUser>>().GetSecurityStampAsync(user)));
        await ctx.SignInAsync(AuthSchemes.Partial, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = false,
            AllowRefresh = false,
            IssuedUtc = window.IssuedUtc,
            ExpiresUtc = window.ExpiresUtc,
        });
    }

    private const string StepProvider = "maldex";
    private const string StepToken = "TotpLastStep";
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// Checks a 6-digit code against the user's authenticator key (RFC 6238, SHA-1, 30 s, one step either side) and refuses a
    /// step that is not newer than the last one accepted. With <paramref name="record"/> the matched step becomes the last accepted.
    /// </summary>
    private static async Task<bool> AcceptTotpAsync(UserManager<AppUser> users, TimeProvider time, AppUser user, string code, bool record)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return false;
        if (await users.GetAuthenticatorKeyAsync(user) is not { Length: > 0 } key) return false;
        var secret = Base32Decode(key);
        var current = time.GetUtcNow().ToUnixTimeSeconds() / 30;
        var last = long.TryParse(await users.GetAuthenticationTokenAsync(user, StepProvider, StepToken), out var l) ? l : -1;
        var matched = -1L;
        for (var step = current - 1; step <= current + 1; step++)
            if (CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(CodeFor(secret, step)), System.Text.Encoding.ASCII.GetBytes(code)))
                matched = step;
        if (matched < 0 || matched <= last) return false;
        if (!record) return true;
        return (await users.SetAuthenticationTokenAsync(user, StepProvider, StepToken, matched.ToString(System.Globalization.CultureInfo.InvariantCulture))).Succeeded;
    }

    private static string CodeFor(byte[] secret, long step)
    {
        Span<byte> message = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { message[i] = (byte)(step & 0xff); step >>= 8; }
        var hash = HMACSHA1.HashData(secret, message);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32Decode(string text)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('=').ToUpperInvariant())
        {
            var v = Base32Alphabet.IndexOf(c);
            if (v < 0) continue;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)((buffer >> bits) & 0xff)); }
        }
        return [.. bytes];
    }

    /// <summary>The 6-digit code typed by the user, or a recovery code, with spaces and dashes removed.</summary>
    private static string Clean(string? code) => (code ?? "").Replace(" ", "").Replace("-", "").Trim();

    private static async Task<IResult> LoginTotpAsync(TotpCodeRequest req, HttpContext ctx, UserManager<AppUser> users, MaldexDbContext db,
        IOptions<MaldexAuthOptions> options, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        if (await LoginFlow.PartialUserAsync(ctx) is not ({ } user, LoginSteps.Totp)) return Unauthenticated();
        var name = user.UserName ?? "";

        if (await users.IsLockedOutAsync(user)) return AuthEndpoints.Error(StatusCodes.Status401Unauthorized, "locked");

        var code = Clean(req.Code);
        var valid = code.Length > 0
            && (code.Length == 6
                ? await AcceptTotpAsync(users, time, user, code, record: true)
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
        await users.RemoveAuthenticationTokenAsync(user, StepProvider, StepToken);
        if (!reset.Succeeded) return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "invalid");
        var key = await users.GetAuthenticatorKeyAsync(user) ?? "";
        // Resetting the key refreshes the security stamp; keep this sign-in alive.
        await ReissueAsync(ctx, user, step);
        var uri = $"otpauth://totp/Maldex:{Uri.EscapeDataString(user.UserName ?? "")}?secret={key}&issuer=Maldex&digits=6&period=30";
        return Results.Ok(new { sharedKey = key, otpauthUri = uri });
    }

    private static async Task<IResult> ConfirmAsync(TotpCodeRequest req, HttpContext ctx, UserManager<AppUser> users, MaldexDbContext db,
        IOptions<MaldexAuthOptions> options, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        if (await EnrolUserAsync(ctx) is not ({ } user, var step)) return Unauthenticated();
        if (await users.GetTwoFactorEnabledAsync(user)) return AuthEndpoints.Error(StatusCodes.Status409Conflict, "totp_enabled");

        var code = Clean(req.Code);
        if (!await AcceptTotpAsync(users, time, user, code, record: false))
            return AuthEndpoints.Error(StatusCodes.Status400BadRequest, "invalid");

        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = await ReplaceRecoveryCodesAsync(ctx, users, user);
        // Other sessions end at their next stamp check, except while signing in (the pending cookie is the only one).
        if (step == LoginSteps.Done) await users.UpdateSecurityStampAsync(user);
        await audit.WriteAsync("auth.totp_enabled", user.UserName ?? "", null, user.Id, ct);

        var next = await LoginFlow.NextAsync(user, options.Value, await AuthEndpoints.SettingsAsync(db, ct), users, totpVerified: true);
        if (step == LoginSteps.Done) await LoginFlow.SignInFullAsync(ctx, user, LoginFlow.SessionIssuedAt(ctx));
        else if (next == LoginSteps.Done) await LoginFlow.SignInFullAsync(ctx, user);
        else await ReissueAsync(ctx, user, next);
        return Results.Ok(new { recoveryCodes = codes, next = step == LoginSteps.Done ? LoginSteps.Done : next });
    }

    private static async Task<IResult> RemoveAsync([Microsoft.AspNetCore.Mvc.FromBody] PasswordRequest req, HttpContext ctx, UserManager<AppUser> users, MaldexDbContext db,
        IOptions<MaldexAuthOptions> options, AuditWriter audit, CancellationToken ct)
    {
        if (await FullSessionUserAsync(ctx) is not { } user) return Unauthenticated();

        if (!await users.GetTwoFactorEnabledAsync(user)) return AuthEndpoints.Error(StatusCodes.Status409Conflict, "totp_not_enabled");
        var settings = await AuthEndpoints.SettingsAsync(db, ct);
        if (settings.RequireTotpForLocal || (!options.Value.PasswordLogin && user.IsBreakGlass))
            return AuthEndpoints.Error(StatusCodes.Status409Conflict, "totp_required");
        if (await CheckPasswordAsync(users, user, req.Password, audit, ct) is { } refusal) return refusal;

        await users.SetTwoFactorEnabledAsync(user, false);
        await users.RemoveAuthenticationTokenAsync(user, MaldexUserStore.InternalLoginProvider, MaldexUserStore.AuthenticatorKeyTokenName);
        await users.RemoveAuthenticationTokenAsync(user, MaldexUserStore.InternalLoginProvider, MaldexUserStore.RecoveryCodesTokenName);
        await users.RemoveAuthenticationTokenAsync(user, StepProvider, StepToken);
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
        if (!string.IsNullOrEmpty(password) && await users.CheckPasswordAsync(user, password))
        {
            await users.ResetAccessFailedCountAsync(user);
            return null;
        }
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

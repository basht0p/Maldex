using System.Globalization;
using System.Security.Claims;
using Maldex.Core.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Maldex.Web.Auth;

/// <summary>The sign-in state machine: which step comes next, and the cookies that carry a finished or unfinished sign-in.</summary>
public static class LoginFlow
{
    /// <summary>
    /// The next step for <paramref name="user"/> after a verified password (or SSO callback): <c>totp</c> when TOTP is on;
    /// otherwise <c>change-password</c> when the user must change it; otherwise <c>enrol-totp</c> for a local user without TOTP
    /// when TOTP is required for local accounts, or when the user is break-glass and password login is off; otherwise <c>done</c>.
    /// Pass <paramref name="totpVerified"/> after a successful TOTP step (and on the later steps), which skips <c>totp</c>.
    /// </summary>
    public static async Task<string> NextAsync(AppUser user, MaldexAuthOptions o, SystemSettings s, UserManager<AppUser> um, bool totpVerified = false)
    {
        var totpEnabled = await um.GetTwoFactorEnabledAsync(user);
        if (totpEnabled && !totpVerified) return LoginSteps.Totp;
        if (user.MustChangePassword) return LoginSteps.ChangePassword;
        if (user.Kind == UserKind.Local && !totpEnabled && (s.RequireTotpForLocal || (!o.PasswordLogin && user.IsBreakGlass)))
            return LoginSteps.EnrolTotp;
        return LoginSteps.Done;
    }

    /// <summary>
    /// Issues the session cookie (claims: user id, security stamp, <c>maldex:iat</c>, <c>maldex:method</c> = <c>password</c> for
    /// a local user or <c>sso</c> for an SSO user), deletes the partial cookie and records <see cref="AppUser.LastSignInAt"/>.
    /// </summary>
    public static async Task SignInFullAsync(HttpContext ctx, AppUser user)
    {
        var services = ctx.RequestServices;
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        user.LastSignInAt = now;
        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded) throw new InvalidOperationException("Could not record the sign-in: " + string.Join("; ", updated.Errors.Select(e => e.Code)));

        var identity = new ClaimsIdentity(AuthSchemes.Application, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.UserName ?? ""));
        identity.AddClaim(new Claim(StampClaimType(services), await users.GetSecurityStampAsync(user)));
        identity.AddClaim(new Claim(MaldexClaims.IssuedAt, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        identity.AddClaim(new Claim(MaldexClaims.Method, user.Kind == UserKind.Sso ? "sso" : "password"));

        await ctx.SignInAsync(AuthSchemes.Application, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
        await ctx.SignOutAsync(AuthSchemes.Partial);
    }

    /// <summary>
    /// Issues the 10-minute partial cookie for an unfinished sign-in whose next step is <paramref name="next"/> (claims: user id,
    /// <c>maldex:next</c>, and the security stamp, so a password reset or disable mid-way ends it). Any session cookie is removed.
    /// </summary>
    public static async Task SignInPartialAsync(HttpContext ctx, AppUser user, string next)
    {
        var services = ctx.RequestServices;
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        var identity = new ClaimsIdentity(AuthSchemes.Partial);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(MaldexClaims.Next, next));
        identity.AddClaim(new Claim(StampClaimType(services), await users.GetSecurityStampAsync(user)));

        await ctx.SignInAsync(AuthSchemes.Partial, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = false,
            AllowRefresh = false,
            IssuedUtc = now,
            ExpiresUtc = now + SessionLifetime.PartialLifetime,
        });
        await ctx.SignOutAsync(AuthSchemes.Application);
    }

    /// <summary>
    /// The user of the partial cookie with its pending step; failing that, the signed-in session user with <c>done</c> (for the
    /// steps a full session may also do). Null when neither is present, or the user is gone, disabled or has a new stamp.
    /// </summary>
    public static async Task<(AppUser user, string next)?> PartialUserAsync(HttpContext ctx)
    {
        var services = ctx.RequestServices;
        var users = services.GetRequiredService<UserManager<AppUser>>();

        var partial = await ctx.AuthenticateAsync(AuthSchemes.Partial);
        if (partial is { Succeeded: true, Principal: { } p })
        {
            var next = p.FindFirstValue(MaldexClaims.Next);
            var user = await users.GetUserAsync(p);
            if (user is not null && !user.IsDisabled && !string.IsNullOrEmpty(next)
                && await users.GetSecurityStampAsync(user) == p.FindFirstValue(StampClaimType(services)))
                return (user, next);
            return null;
        }

        if (ctx.User.Identity is { IsAuthenticated: true } identity && identity.AuthenticationType == AuthSchemes.Application)
        {
            var current = services.GetRequiredService<CurrentUser>();
            await current.ResolveAsync(ctx.User, ctx.RequestAborted);
            if (current.User is { } user) return (user, LoginSteps.Done);
        }
        return null;
    }

    private static string StampClaimType(IServiceProvider services) =>
        services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
}

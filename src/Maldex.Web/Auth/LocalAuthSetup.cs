using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Maldex.Core.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Maldex.Web.Auth;

/// <summary>ASP.NET Core Identity for local accounts, the session cookies, the key ring and the sign-in rate limit.</summary>
public static class LocalAuthSetup
{
    /// <summary>The rate-limiter policy of <c>POST /api/auth/login</c> and <c>POST /api/auth/login/totp</c>.</summary>
    public const string LoginRateLimitPolicy = "maldex-login";

    public const string ApplicationCookieName = "maldex";
    public const string PartialCookieName = "maldex-partial";

    /// <summary>Characters allowed in a user name (<c>^[A-Za-z0-9._@-]{1,64}$</c>).</summary>
    public const string UserNameCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._@-";
    public const int MaxUserNameLength = 64;
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 256;

    /// <summary>
    /// Registers <see cref="UserManager{TUser}"/> for <see cref="AppUser"/> over <see cref="MaldexUserStore"/>, with the Maldex
    /// password (12–256 characters, no composition rules), lockout (5 failures, 15 minutes) and user-name rules, the default
    /// token providers and <see cref="TimeProvider"/>. Used by the web host and by the admin CLI.
    /// </summary>
    public static IServiceCollection AddMaldexIdentityCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDataProtection();
        services.AddIdentityCore<AppUser>(o =>
            {
                o.Password.RequiredLength = MinPasswordLength;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredUniqueChars = 1;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
                o.User.AllowedUserNameCharacters = UserNameCharacters;
                o.User.RequireUniqueEmail = false;
            })
            .AddUserStore<MaldexUserStore>()
            .AddUserValidator<UserNameLengthValidator>()
            .AddPasswordValidator<PasswordMaxLengthValidator>()
            .AddDefaultTokenProviders();
        return services;
    }

    /// <summary>
    /// The Data Protection key ring that encrypts the cookies: <c>Maldex:Auth:KeysDir</c> when set, else <c>&lt;Maldex:Real:Root&gt;/keys</c>
    /// in Real mode with a Root, else <c>&lt;content root&gt;/.data/keys</c>. The directory is created (or tightened) to mode 0700.
    /// </summary>
    public static string KeysDirectory(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Maldex:Auth:KeysDir"];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured, environment.ContentRootPath);
        var realRoot = configuration["Maldex:Real:Root"];
        if (configuration["Maldex:Mode"] == "Real" && !string.IsNullOrWhiteSpace(realRoot) && Path.IsPathRooted(realRoot))
            return Path.Combine(realRoot, "keys");
        return Path.Combine(environment.ContentRootPath, ".data", "keys");
    }

    public static void AddMaldexDataProtection(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var dir = KeysDirectory(configuration, environment);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
        }
        else
        {
            const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(dir, owner);
            // An existing directory (or one the umask widened) is tightened too: the keys decrypt every session.
            if (File.GetUnixFileMode(dir) != owner) File.SetUnixFileMode(dir, owner);
        }
        services.AddDataProtection().SetApplicationName("Maldex").PersistKeysToFileSystem(new DirectoryInfo(dir));
    }

    /// <summary>The session cookie and the partial cookie. Neither ever redirects: 401 when not signed in, 403 when refused.</summary>
    public static AuthenticationBuilder AddMaldexCookies(this AuthenticationBuilder builder)
    {
        builder.AddCookie(AuthSchemes.Application, o =>
        {
            o.Cookie.Name = ApplicationCookieName;
            o.Cookie.Path = "/";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.CookieManager = new HostPrefixCookieManager();
            o.ExpireTimeSpan = SessionLifetime.SlidingExpiry;
            o.SlidingExpiration = true;
            o.Events = NoRedirectEvents(ValidateSessionAsync);
        });
        builder.AddCookie(AuthSchemes.Partial, o =>
        {
            o.Cookie.Name = PartialCookieName;
            o.Cookie.Path = "/";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = SessionLifetime.PartialLifetime;
            o.SlidingExpiration = false;
            o.Events = NoRedirectEvents(null);
        });
        return builder;
    }

    private static CookieAuthenticationEvents NoRedirectEvents(Func<CookieValidatePrincipalContext, Task>? validate)
    {
        var events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; },
            OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; },
            OnRedirectToLogout = ctx => { ctx.Response.StatusCode = StatusCodes.Status200OK; return Task.CompletedTask; },
            OnRedirectToReturnUrl = ctx => { ctx.Response.StatusCode = StatusCodes.Status200OK; return Task.CompletedTask; },
        };
        if (validate is not null) events.OnValidatePrincipal = validate;
        return events;
    }

    /// <summary>
    /// Every request with a session cookie: re-checks the security stamp against the user row once per
    /// <see cref="MaldexAuthOptions.StampValidationInterval"/> (then renews the cookie, which restarts the interval), and
    /// refuses a session older than <see cref="SessionLifetime.AbsoluteCap"/>. A disabled user is refused on every request by
    /// <see cref="CurrentUser"/>, without waiting for the stamp check.
    /// </summary>
    internal static async Task ValidateSessionAsync(CookieValidatePrincipalContext ctx)
    {
        var services = ctx.HttpContext.RequestServices;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var principal = ctx.Principal;

        if (!long.TryParse(principal?.FindFirstValue(MaldexClaims.IssuedAt), NumberStyles.None, CultureInfo.InvariantCulture, out var iat)
            || SessionLifetime.IsBeyondAbsoluteCap(DateTimeOffset.FromUnixTimeSeconds(iat), now))
        {
            await RejectAsync(ctx);
            return;
        }

        var interval = services.GetRequiredService<IOptions<MaldexAuthOptions>>().Value.StampValidationInterval;
        if (ctx.Properties.IssuedUtc is { } issued && now - issued < interval) return;

        var users = services.GetRequiredService<UserManager<AppUser>>();
        var stampClaim = services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
        var user = await users.GetUserAsync(principal!);
        if (user is null || user.IsDisabled || await users.GetSecurityStampAsync(user) != principal!.FindFirstValue(stampClaim))
        {
            await RejectAsync(ctx);
            return;
        }
        ctx.ShouldRenew = true;
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(AuthSchemes.Application);
    }

    /// <summary>
    /// 10 requests a minute (<see cref="MaldexAuthOptions.LoginRateLimitPerMinute"/>) per client IP to the sign-in endpoints; then
    /// 429 <c>{"error":"rate_limited"}</c>. Requests without a remote address (in-process test servers) share one bucket.
    /// </summary>
    public static IServiceCollection AddMaldexLoginRateLimit(this IServiceCollection services, int permitsPerMinute)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.HttpContext.Response.WriteAsJsonAsync(new { error = "rate_limited" }, ct);
            };
            o.AddPolicy(LoginRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, permitsPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
        });
        return services;
    }
}

/// <summary>Names the session cookie <c>__Host-maldex</c> on HTTPS requests (Secure, Path=/, no Domain) and <c>maldex</c> otherwise.</summary>
public sealed class HostPrefixCookieManager : ICookieManager
{
    private readonly ChunkingCookieManager _inner = new();

    private static string Name(HttpContext ctx, string key) => ctx.Request.IsHttps ? "__Host-" + key : key;

    public string? GetRequestCookie(HttpContext context, string key) => _inner.GetRequestCookie(context, Name(context, key));

    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options) =>
        _inner.AppendResponseCookie(context, Name(context, key), value, options);

    public void DeleteCookie(HttpContext context, string key, CookieOptions options) =>
        _inner.DeleteCookie(context, Name(context, key), options);
}

/// <summary>User names are at most <see cref="LocalAuthSetup.MaxUserNameLength"/> characters (the character set is Identity's option).</summary>
public sealed class UserNameLengthValidator : IUserValidator<AppUser>
{
    public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user) =>
        Task.FromResult(user.UserName is { Length: > 0 and <= LocalAuthSetup.MaxUserNameLength }
            ? IdentityResult.Success
            : IdentityResult.Failed(new IdentityError { Code = "InvalidUserNameLength", Description = $"User names are 1 to {LocalAuthSetup.MaxUserNameLength} characters." }));
}

/// <summary>Passwords are at most <see cref="LocalAuthSetup.MaxPasswordLength"/> characters (the minimum is Identity's option).</summary>
public sealed class PasswordMaxLengthValidator : IPasswordValidator<AppUser>
{
    public const string Code = "PasswordTooLong";

    public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password) =>
        Task.FromResult(password is { Length: > LocalAuthSetup.MaxPasswordLength }
            ? IdentityResult.Failed(new IdentityError { Code = Code, Description = $"Passwords are at most {LocalAuthSetup.MaxPasswordLength} characters." })
            : IdentityResult.Success);
}

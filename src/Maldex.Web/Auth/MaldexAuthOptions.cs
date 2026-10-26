using Microsoft.AspNetCore.Identity;

namespace Maldex.Web.Auth;

/// <summary>Bound from <c>Maldex:Auth</c>.</summary>
public sealed class MaldexAuthOptions
{
    public const string SectionName = "Maldex:Auth";

    /// <summary><c>Local</c> (the default, and the only mode outside Development and Testing) or <c>Dev</c>.</summary>
    public string Mode { get; set; } = "Local";

    /// <summary>False: only break-glass local Admins may sign in with a password; everyone else uses SSO.</summary>
    public bool PasswordLogin { get; set; } = true;

    /// <summary>Dev mode only: the role of the fixed dev user when no <c>X-Dev-Role</c> header is sent.</summary>
    public string DevRole { get; set; } = "Admin";

    /// <summary>How often a session cookie's security stamp is re-checked against the user row.</summary>
    public TimeSpan StampValidationInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Requests per minute per client IP to the sign-in endpoints.</summary>
    public int LoginRateLimitPerMinute { get; set; } = 10;

    /// <summary>The Data Protection key directory; overrides <c>&lt;Maldex:Real:Root&gt;/keys</c> and <c>&lt;content root&gt;/.data/keys</c>.</summary>
    public string? KeysDir { get; set; }

    public OidcOptions Oidc { get; set; } = new();

    public bool IsDev => string.Equals(Mode, "Dev", StringComparison.OrdinalIgnoreCase);

    /// <summary>The options bound from <c>Maldex:Auth</c> in <paramref name="configuration"/> (see <see cref="Bind"/>).</summary>
    public static MaldexAuthOptions Read(IConfiguration configuration)
    {
        var o = new MaldexAuthOptions();
        Bind(configuration.GetSection(SectionName), o);
        return o;
    }

    /// <summary>
    /// Binds <paramref name="section"/> onto <paramref name="o"/>. The configuration binder appends a configured array to the
    /// property's default items; a configured list here replaces them instead (so <c>Scopes: ["openid"]</c> means just openid).
    /// </summary>
    public static void Bind(IConfigurationSection section, MaldexAuthOptions o)
    {
        section.Bind(o);
        if (Configured(section.GetSection("Oidc:Scopes")) is { } scopes) o.Oidc.Scopes = scopes;
        if (Configured(section.GetSection("Oidc:AdminValues")) is { } admin) o.Oidc.AdminValues = admin;
        if (Configured(section.GetSection("Oidc:AnalystValues")) is { } analyst) o.Oidc.AnalystValues = analyst;
    }

    private static string[]? Configured(IConfigurationSection list) =>
        list.GetChildren().Any() ? list.Get<string[]>() ?? [] : null;
}

/// <summary>Bound from <c>Maldex:Auth:Oidc</c>: the optional generic OpenID Connect sign-in.</summary>
public sealed class OidcOptions
{
    public bool Enabled { get; set; }
    public string DisplayName { get; set; } = "Single sign-on";
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];
    public string NameClaim { get; set; } = "name";
    public string RoleClaim { get; set; } = "";
    public string[] AdminValues { get; set; } = [];
    public string[] AnalystValues { get; set; } = [];
}

/// <summary>The cookie authentication schemes.</summary>
public static class AuthSchemes
{
    /// <summary>The signed-in session (cookie <c>__Host-maldex</c> on HTTPS, <c>maldex</c> otherwise).</summary>
    public static readonly string Application = IdentityConstants.ApplicationScheme;

    /// <summary>A sign-in that still has a step to do (TOTP, password change, TOTP enrolment); cookie <c>maldex-partial</c>, 10 minutes.</summary>
    public const string Partial = "Maldex.Partial";
}

/// <summary>Maldex-specific claims on the session cookies.</summary>
public static class MaldexClaims
{
    /// <summary>When the user signed in (Unix seconds); the absolute session cap counts from it.</summary>
    public const string IssuedAt = "maldex:iat";

    /// <summary><c>password</c> or <c>sso</c>.</summary>
    public const string Method = "maldex:method";

    /// <summary>On the partial cookie: the step the user must do next.</summary>
    public const string Next = "maldex:next";
}

/// <summary>The values of <c>next</c> in the sign-in answers.</summary>
public static class LoginSteps
{
    public const string Done = "done";
    public const string Totp = "totp";
    public const string ChangePassword = "change-password";
    public const string EnrolTotp = "enrol-totp";
}

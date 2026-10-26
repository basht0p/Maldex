namespace Maldex.Web.Auth;

/// <summary>Fail-fast checks of <c>Maldex:Auth</c>; Program.cs prints the problem and exits 78 (<see cref="StartupChecks.ConfigErrorExitCode"/>).</summary>
public static class AuthStartupValidation
{
    /// <summary>Null when the sign-in configuration is usable; otherwise a one-line reason with a hint.</summary>
    public static string? Problem(IConfiguration configuration, IHostEnvironment environment)
    {
        var mode = configuration["Maldex:Auth:Mode"];
        if (string.IsNullOrWhiteSpace(mode)) mode = "Local";
        if (string.Equals(mode, "Dev", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
                return $"Dev auth is not allowed in {environment.EnvironmentName}; set Maldex:Auth:Mode to Local (or remove it)";
        }
        else if (!string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase))
        {
            return $"Maldex:Auth:Mode '{mode}' is not supported; use Local (see README \"Single sign-on (optional)\")";
        }

        if (Flag(configuration, "Maldex:Auth:PasswordLogin", true) is not { } passwordLogin)
            return "Maldex:Auth:PasswordLogin must be true or false";
        if (Flag(configuration, "Maldex:Auth:Oidc:Enabled", false) is not { } oidcEnabled)
            return "Maldex:Auth:Oidc:Enabled must be true or false";
        if (!passwordLogin && !oidcEnabled)
            return "Maldex:Auth:PasswordLogin is false but Maldex:Auth:Oidc is not enabled; nobody could sign in";
        return oidcEnabled ? OidcProblem(MaldexAuthOptions.Read(configuration).Oidc, environment) : null;
    }

    /// <summary>
    /// The rules for an enabled <c>Maldex:Auth:Oidc</c>: an absolute HTTPS <c>Authority</c> (plain HTTP to localhost or 127.0.0.1
    /// only in Development and Testing), a <c>ClientId</c>, and <c>AdminValues</c> whenever a <c>RoleClaim</c> is mapped. Discovery is
    /// not fetched here, so an IdP outage does not stop Maldex starting.
    /// </summary>
    private static string? OidcProblem(OidcOptions o, IHostEnvironment environment)
    {
        var devOrTest = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        if (!Uri.TryCreate(o.Authority?.Trim(), UriKind.Absolute, out var authority)
            || !(authority.Scheme == Uri.UriSchemeHttps || (devOrTest && authority.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(authority))))
            return devOrTest
                ? $"Maldex:Auth:Oidc:Authority '{o.Authority}' must be an absolute https:// URL (or http://localhost / http://127.0.0.1 in {environment.EnvironmentName})"
                : $"Maldex:Auth:Oidc:Authority '{o.Authority}' must be an absolute https:// URL (the IdP's issuer)";
        if (string.IsNullOrWhiteSpace(o.ClientId))
            return "Maldex:Auth:Oidc:ClientId must be set when Maldex:Auth:Oidc:Enabled is true";
        if (!string.IsNullOrWhiteSpace(o.RoleClaim) && !o.AdminValues.Any(v => !string.IsNullOrWhiteSpace(v)))
            return $"Maldex:Auth:Oidc:RoleClaim is '{o.RoleClaim}' but Maldex:Auth:Oidc:AdminValues is empty; nobody could become an Admin through SSO";
        return null;
    }

    private static bool IsLoopbackHost(Uri uri) =>
        string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1";

    /// <summary>The boolean at <paramref name="key"/>, <paramref name="fallback"/> when unset, null when it is not a boolean.</summary>
    private static bool? Flag(IConfiguration configuration, string key, bool fallback)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return bool.TryParse(value, out var b) ? b : null;
    }
}

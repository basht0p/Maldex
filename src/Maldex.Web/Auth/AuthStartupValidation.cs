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
        return null;
    }

    /// <summary>The boolean at <paramref name="key"/>, <paramref name="fallback"/> when unset, null when it is not a boolean.</summary>
    private static bool? Flag(IConfiguration configuration, string key, bool fallback)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return bool.TryParse(value, out var b) ? b : null;
    }
}

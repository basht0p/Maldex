using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Maldex.Core.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Maldex.Web.Auth;

public sealed class DevAuthOptions
{
    public string DevRole { get; set; } = "Admin";
}

/// <summary>
/// Development/Testing-only authentication. Always signs in as the fixed dev user unless
/// <c>X-Dev-User</c> / <c>X-Dev-Role</c> headers are present (honored only in Development and Testing).
/// </summary>
public sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHostEnvironment env,
    IOptions<DevAuthOptions> devOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Dev";

    /// <summary>The Users.UserName of the dev account for a display name: <c>dev-</c> plus 12 hex characters of SHA-256("dev:" + name).</summary>
    public static string DevUserName(string name) =>
        "dev-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("dev:" + name)))[..12].ToLowerInvariant();

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
            return Task.FromResult(AuthenticateResult.Fail($"Dev auth is not allowed in {env.EnvironmentName}"));

        var name = "Dev User";
        var role = ParseRole(devOptions.Value.DevRole) ?? UserRole.Admin;

        var user = Request.Headers["X-Dev-User"].ToString();
        if (!string.IsNullOrWhiteSpace(user)) name = user;
        role = ParseRole(Request.Headers["X-Dev-Role"].ToString()) ?? role;

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, name),
            new Claim(AuthSetup.DevRoleClaim, role.ToString()),
        ], SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static UserRole? ParseRole(string? value) =>
        Enum.TryParse<UserRole>(value, ignoreCase: true, out var r) && Enum.IsDefined(r) ? r : null;
}

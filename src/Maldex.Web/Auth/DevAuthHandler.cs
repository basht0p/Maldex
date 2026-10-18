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
    public static readonly Guid DefaultOid = Guid.Parse("00000000-0000-0000-0000-000000000001");

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (env.IsProduction())
            return Task.FromResult(AuthenticateResult.Fail("Dev auth is not allowed in Production"));

        var honorHeaders = env.IsDevelopment() || env.IsEnvironment("Testing");
        var oid = DefaultOid;
        var name = "Dev User";
        var role = ParseRole(devOptions.Value.DevRole) ?? UserRole.Admin;

        if (honorHeaders)
        {
            var user = Request.Headers["X-Dev-User"].ToString();
            if (!string.IsNullOrWhiteSpace(user))
            {
                name = user;
                oid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("dev:" + user)).AsSpan(0, 16));
            }
            role = ParseRole(Request.Headers["X-Dev-Role"].ToString()) ?? role;
        }

        var appRole = role == UserRole.Admin ? AuthSetup.AdminRole : AuthSetup.AnalystRole;
        var identity = new ClaimsIdentity(
        [
            new Claim("oid", oid.ToString()),
            new Claim(ClaimTypes.Name, name),
            new Claim("roles", appRole),
        ], SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static UserRole? ParseRole(string? value) =>
        Enum.TryParse<UserRole>(value, ignoreCase: true, out var r) && Enum.IsDefined(r) ? r : null;
}

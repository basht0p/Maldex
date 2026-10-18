using System.Security.Claims;
using Maldex.Core.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Identity.Web;

namespace Maldex.Web.Auth;

public static class AuthSetup
{
    public const string AnalystPolicy = "Analyst";
    public const string AdminPolicy = "Admin";
    public const string AnalystRole = "Maldex.Analyst";
    public const string AdminRole = "Maldex.Admin";

    /// <summary>
    /// Registers authentication (Dev or Entra), the role policies and <see cref="CurrentUser"/>.
    /// Throws when dev auth is configured in Production.
    /// </summary>
    public static WebApplicationBuilder AddMaldexAuth(this WebApplicationBuilder builder)
    {
        var mode = builder.Configuration["Maldex:Auth:Mode"] ?? "Entra";
        var isDev = string.Equals(mode, "Dev", StringComparison.OrdinalIgnoreCase);
        if (isDev && builder.Environment.IsProduction())
            throw new InvalidOperationException("Dev auth is not allowed in Production");

        builder.Services.Configure<DevAuthOptions>(builder.Configuration.GetSection("Maldex:Auth"));
        if (isDev)
        {
            builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);
        }
        else
        {
            builder.Services.AddAuthentication().AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
        }

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(AnalystPolicy, p => p.RequireAuthenticatedUser().RequireAssertion(c => MapRole(c.User) is not null))
            .AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().RequireAssertion(c => MapRole(c.User) == UserRole.Admin));

        builder.Services.AddScoped<CurrentUser>();
        builder.Services.AddScoped<Audit.AuditWriter>();
        return builder;
    }

    /// <summary>Maps the "roles" claim (Entra app roles) to <see cref="UserRole"/>; Admin wins. Null means no Maldex role.</summary>
    public static UserRole? MapRole(ClaimsPrincipal user)
    {
        var roles = user.FindAll("roles").Concat(user.FindAll(ClaimTypes.Role)).Select(c => c.Value).ToHashSet();
        if (roles.Contains(AdminRole)) return UserRole.Admin;
        if (roles.Contains(AnalystRole)) return UserRole.Analyst;
        return null;
    }

    public static string? ObjectId(ClaimsPrincipal user) =>
        user.FindFirstValue("oid") ?? user.FindFirstValue(ClaimConstants.ObjectId) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
}

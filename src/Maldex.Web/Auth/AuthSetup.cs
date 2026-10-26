using System.Security.Claims;
using Maldex.Core.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;

namespace Maldex.Web.Auth;

public static class AuthSetup
{
    public const string AnalystPolicy = "Analyst";
    public const string AdminPolicy = "Admin";

    /// <summary>Claim the Dev scheme uses to carry the role it provisions the dev user with.</summary>
    public const string DevRoleClaim = "maldex:dev-role";

    /// <summary>
    /// Registers authentication (Dev or Entra), the role policies and <see cref="CurrentUser"/>.
    /// Throws when dev auth is configured in Production.
    /// </summary>
    public static WebApplicationBuilder AddMaldexAuth(this WebApplicationBuilder builder)
    {
        var mode = builder.Configuration["Maldex:Auth:Mode"] ?? "Entra";
        var isDev = string.Equals(mode, "Dev", StringComparison.OrdinalIgnoreCase);
        if (isDev)
        {
            if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
                throw new InvalidOperationException($"Dev auth is not allowed in {builder.Environment.EnvironmentName}");
            var devRole = builder.Configuration["Maldex:Auth:DevRole"];
            if (!string.IsNullOrEmpty(devRole) && !(Enum.TryParse<UserRole>(devRole, true, out var r) && Enum.IsDefined(r)))
                throw new InvalidOperationException($"Invalid Maldex:Auth:DevRole '{devRole}' (expected Admin or Analyst)");
        }

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
            .AddPolicy(AnalystPolicy, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(UserRole.Analyst)))
            .AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(UserRole.Admin)));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IAuthorizationHandler, RoleRequirementHandler>();

        builder.Services.AddScoped<CurrentUser>();
        builder.Services.AddScoped<Audit.AuditWriter>();
        return builder;
    }
}

/// <summary>Satisfied when the signed-in user's Maldex role is at least <see cref="Minimum"/> (Admin satisfies Analyst).</summary>
public sealed class RoleRequirement(UserRole minimum) : IAuthorizationRequirement
{
    public UserRole Minimum { get; } = minimum;
}

public sealed class RoleRequirementHandler(CurrentUser current, IHttpContextAccessor http) : AuthorizationHandler<RoleRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleRequirement requirement)
    {
        // Hub invocations run in their own scope, where CurrentUserMiddleware has not run; resolving is idempotent.
        await current.ResolveAsync(context.User, http.HttpContext?.RequestAborted ?? CancellationToken.None);
        if (current.Role is { } role && role >= requirement.Minimum) context.Succeed(requirement);
    }
}

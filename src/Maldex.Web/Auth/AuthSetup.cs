using System.Security.Claims;
using Maldex.Core.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace Maldex.Web.Auth;

public static class AuthSetup
{
    public const string AnalystPolicy = "Analyst";
    public const string AdminPolicy = "Admin";

    /// <summary>Claim the Dev scheme uses to carry the role it provisions the dev user with.</summary>
    public const string DevRoleClaim = "maldex:dev-role";

    /// <summary>
    /// Registers authentication (the Local cookie sessions, or Dev), Identity for local accounts, the key ring, the sign-in
    /// rate limit, the role policies and <see cref="CurrentUser"/>. <see cref="AuthStartupValidation.Problem"/> has already
    /// refused unsupported modes; Dev outside Development and Testing still throws here as a second line.
    /// </summary>
    public static WebApplicationBuilder AddMaldexAuth(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(MaldexAuthOptions.SectionName);
        var auth = MaldexAuthOptions.Read(builder.Configuration);
        var isDev = auth.IsDev;
        if (isDev)
        {
            if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
                throw new InvalidOperationException($"Dev auth is not allowed in {builder.Environment.EnvironmentName}");
            var devRole = builder.Configuration["Maldex:Auth:DevRole"];
            if (!string.IsNullOrEmpty(devRole) && !(Enum.TryParse<UserRole>(devRole, true, out var r) && Enum.IsDefined(r)))
                throw new InvalidOperationException($"Invalid Maldex:Auth:DevRole '{devRole}' (expected Admin or Analyst)");
        }

        builder.Services.AddOptions<MaldexAuthOptions>().Configure(o => MaldexAuthOptions.Bind(section, o));
        builder.Services.Configure<DevAuthOptions>(section);
        builder.Services.AddMaldexIdentityCore(builder.Configuration);
        builder.Services.AddMaldexDataProtection(builder.Configuration, builder.Environment);
        builder.Services.AddMaldexLoginRateLimit(auth.LoginRateLimitPerMinute);

        // The cookie schemes exist in both modes (sign-out and the sign-in endpoints always work); only the default differs.
        var authentication = builder.Services.AddAuthentication(isDev ? DevAuthHandler.SchemeName : AuthSchemes.Application);
        if (isDev) authentication.AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);
        authentication.AddMaldexCookies();
        OidcSetup.AddMaldexOidc(authentication, auth);

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(AnalystPolicy, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(UserRole.Analyst)))
            .AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(UserRole.Admin)));
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, NoRoleResultHandler>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IAuthorizationHandler, RoleRequirementHandler>();

        builder.Services.AddScoped<CurrentUser>();
        builder.Services.AddScoped<Audit.AuditWriter>();
        return builder;
    }

    /// <summary>Maps <c>/api/auth</c> (sign-in, password, TOTP) and the user administration endpoints.</summary>
    public static IEndpointRouteBuilder MapMaldexAuthEndpoints(this IEndpointRouteBuilder app)
    {
        AuthEndpoints.Map(app);
        TotpEndpoints.Map(app);
        UserAdminEndpoints.Map(app);
        return app;
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

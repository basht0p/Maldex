using Maldex.Web.Auth;

namespace Maldex.Web.Endpoints;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", (CurrentUser user) => new { id = user.UserId, displayName = user.DisplayName, role = user.Role })
            .RequireAuthorization(AuthSetup.AnalystPolicy);
        return app;
    }
}

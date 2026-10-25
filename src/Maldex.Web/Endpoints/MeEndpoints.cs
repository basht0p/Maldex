using Maldex.Web.Auth;
using Microsoft.Extensions.Options;

namespace Maldex.Web.Endpoints;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        // maxUploadBytes is the effective submit cap, so the UI can refuse an oversized file before uploading it.
        app.MapGet("/api/me", (CurrentUser user, IOptions<SubmissionOptions> submission) =>
                new { id = user.UserId, displayName = user.DisplayName, role = user.Role, maxUploadBytes = submission.Value.MaxUploadBytes })
            .RequireAuthorization(AuthSetup.AnalystPolicy);
        return app;
    }
}

using Maldex.Core.Domain;
using Maldex.Web.Auth;
using Microsoft.Extensions.Options;

namespace Maldex.Web.Endpoints;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        // Any signed-in user, with or without a role (role null: the SPA shows the waiting page).
        // maxUploadBytes is the effective submit cap, so the UI can refuse an oversized file before uploading it.
        app.MapGet("/api/me", (CurrentUser current, IOptions<SubmissionOptions> submission, IOptions<MaldexAuthOptions> auth) =>
            {
                var user = current.User!;
                return new
                {
                    id = user.Id,
                    displayName = user.DisplayName,
                    role = user.Role,
                    maxUploadBytes = submission.Value.MaxUploadBytes,
                    kind = user.Kind,
                    totpEnabled = user.TwoFactorEnabled,
                    isBreakGlass = user.IsBreakGlass,
                    roleManagedByIdp = user.Kind == UserKind.Sso && !string.IsNullOrEmpty(auth.Value.Oidc.RoleClaim),
                };
            })
            .RequireAuthorization();
        return app;
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Maldex.Web.Auth;

/// <summary>
/// A signed-in user without a Maldex role (a pending SSO account, say) who is refused by a policy gets
/// <c>403 {"error":"no_role"}</c>, so the SPA can show the waiting page. Every other outcome is the default handling.
/// </summary>
public sealed class NoRoleResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && context.User.Identity?.IsAuthenticated == true
            && context.RequestServices.GetRequiredService<CurrentUser>() is { IsResolved: true, HasRole: false })
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "no_role" });
            return;
        }
        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

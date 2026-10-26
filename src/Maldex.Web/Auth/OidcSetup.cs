using Microsoft.AspNetCore.Authentication;

namespace Maldex.Web.Auth;

/// <summary>The optional OpenID Connect sign-in (<c>Maldex:Auth:Oidc</c>).</summary>
public static class OidcSetup
{
    public static void AddMaldexOidc(AuthenticationBuilder builder, MaldexAuthOptions options)
    {
    }
}

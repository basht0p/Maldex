using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Maldex.Web.Auth;

/// <summary>The optional OpenID Connect sign-in (<c>Maldex:Auth:Oidc</c>).</summary>
public static class OidcSetup
{
    /// <summary>The OpenID Connect authentication scheme.</summary>
    public const string Scheme = "oidc";

    /// <summary>The handler's sign-in scheme. <see cref="SsoSignIn"/> completes every callback itself, so this cookie is never written.</summary>
    public const string ExternalScheme = "Maldex.External";

    public const string ExternalCookieName = "maldex-external";
    public const string CallbackPath = "/signin-oidc";

    /// <summary>
    /// Registers the <c>oidc</c> scheme when <see cref="OidcOptions.Enabled"/>: authorization code flow with PKCE, response mode
    /// <c>query</c> (so the correlation and nonce cookies can be <c>SameSite=Lax</c> over plain-HTTP localhost), discovery from
    /// <see cref="OidcOptions.Authority"/>, no saved tokens and the IdP's claim names unmapped. The callback never issues the
    /// handler's own cookie: <see cref="SsoSignIn.CompleteAsync"/> provisions the user and issues the Maldex session, then
    /// redirects. A failed callback redirects to <c>/login?error=sso_failed</c>.
    /// </summary>
    public static void AddMaldexOidc(AuthenticationBuilder builder, MaldexAuthOptions options)
    {
        var oidc = options.Oidc;
        if (!oidc.Enabled) return;

        builder.AddCookie(ExternalScheme, o =>
        {
            o.Cookie.Name = ExternalCookieName;
            o.Cookie.Path = "/";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.CookieManager = new HostPrefixCookieManager();
            o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            o.SlidingExpiration = false;
            o.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; },
                OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; },
            };
        });

        builder.AddOpenIdConnect(Scheme, o =>
        {
            o.Authority = oidc.Authority.Trim();
            o.ClientId = oidc.ClientId.Trim();
            o.ClientSecret = string.IsNullOrEmpty(oidc.ClientSecret) ? null : oidc.ClientSecret;
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.ResponseMode = OpenIdConnectResponseMode.Query;
            o.UsePkce = true;
            o.CallbackPath = CallbackPath;
            o.SignInScheme = ExternalScheme;
            o.SaveTokens = false;
            o.MapInboundClaims = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.Scope.Clear();
            foreach (var scope in oidc.Scopes.Where(s => !string.IsNullOrWhiteSpace(s))) o.Scope.Add(scope.Trim());
            if (!o.Scope.Contains("openid")) o.Scope.Add("openid");
            // The issuer names the login provider ("oidc:" + iss); the handler drops the iss claim by default.
            o.ClaimActions.Remove("iss");

            // Lax (not the default None, which browsers refuse without Secure) and Secure only on HTTPS: the callback is a
            // top-level GET (response mode query), which carries Lax cookies, also over the plain-HTTP SSH forward.
            o.CorrelationCookie.SameSite = SameSiteMode.Lax;
            o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.NonceCookie.SameSite = SameSiteMode.Lax;
            o.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

            o.Events = new OpenIdConnectEvents
            {
                OnTicketReceived = async ctx =>
                {
                    var target = await SsoSignIn.CompleteAsync(ctx.HttpContext, ctx.Principal!, oidc, ctx.ReturnUri);
                    ctx.Response.Redirect(target);
                    ctx.HandleResponse();
                },
                OnRemoteFailure = ctx =>
                {
                    ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OidcSetup))
                        .LogWarning(ctx.Failure, "SSO sign-in failed");
                    ctx.Response.Redirect("/login?error=sso_failed");
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                },
            };
        });

        // Discovery and JWKS over plain HTTP only for a local IdP in Development and Testing (startup validation enforces the
        // https Authority everywhere else).
        builder.Services.AddOptions<OpenIdConnectOptions>(Scheme).Configure<IHostEnvironment>((o, env) =>
            o.RequireHttpsMetadata = !(env.IsDevelopment() || env.IsEnvironment("Testing")));
    }
}

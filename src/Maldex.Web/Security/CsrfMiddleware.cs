namespace Maldex.Web.Security;

/// <summary>
/// Cross-site request protection for <c>/api</c> and <c>/hubs</c>, which are authenticated by cookie:
/// <list type="bullet">
/// <item>a request whose <c>Origin</c> header is not this site (<c>{scheme}://{host}</c> after forwarded headers) gets
/// <c>403 {"error":"origin"}</c>; that also covers cross-site WebSocket upgrades;</item>
/// <item>a POST, PUT, PATCH or DELETE without <c>X-Maldex-Csrf: 1</c> gets <c>403 {"error":"csrf"}</c>. A cross-site form cannot
/// set a custom header, and no CORS policy allows one.</item>
/// </list>
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Maldex-Csrf";

    public Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) && !path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
            return next(ctx);

        var origin = ctx.Request.Headers.Origin;
        if (origin.Count > 0 && !(origin.Count == 1 && IsSameOrigin(origin.ToString(), ctx.Request)))
            return RefuseAsync(ctx, "origin");

        var method = ctx.Request.Method;
        if ((HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method))
            && !(ctx.Request.Headers.TryGetValue(HeaderName, out var token) && token.Count == 1 && token[0] == "1"))
            return RefuseAsync(ctx, "csrf");

        return next(ctx);
    }

    /// <summary>Scheme, host and port must all match the request; a missing port means the scheme's default (80 or 443).</summary>
    public static bool IsSameOrigin(string origin, HttpRequest request)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        var https = string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        var port = request.Host.Port ?? (https ? 443 : 80);
        return string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.IdnHost, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == port
            && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static Task RefuseAsync(HttpContext ctx, string error)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return ctx.Response.WriteAsJsonAsync(new { error });
    }
}

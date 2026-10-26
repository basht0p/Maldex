namespace Maldex.Web.Security;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string Csp =
        "default-src 'self'; connect-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext ctx)
    {
        ctx.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;
            // An endpoint may set a stricter policy of its own (the report does); the site-wide one is only the default.
            if (!headers.ContainsKey("Content-Security-Policy")) headers["Content-Security-Policy"] = Csp;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            return Task.CompletedTask;
        }, ctx);
        return next(ctx);
    }
}

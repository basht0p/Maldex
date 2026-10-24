using Maldex.Data.Templates;
using Maldex.Web.Auth;

namespace Maldex.Web.Endpoints;

public sealed record TemplateSummaryDto(string Id, string DisplayName, bool IsDefault);

public static class TemplateEndpoints
{
    public static IEndpointRouteBuilder MapTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/templates");

        g.MapGet("", async (TemplateCatalog catalog, CancellationToken ct) =>
            (await catalog.EnabledAsync(ct)).Select(t => new TemplateSummaryDto(t.Id, t.DisplayName, t.IsDefault)).ToList())
            .RequireAuthorization(AuthSetup.AnalystPolicy);

        return app;
    }
}

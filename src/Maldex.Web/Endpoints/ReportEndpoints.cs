using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Web.Auth;
using Maldex.Web.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace Maldex.Web.Endpoints;

/// <summary>Analyst-facing HTML and PDF reports of a completed task. Not org-scoped (one analyst team).</summary>
public static class ReportEndpoints
{
    /// <summary>The report is static markup plus data: URI images and an inline stylesheet. Nothing else may load or run.</summary>
    public const string ReportCsp = "default-src 'none'; img-src data:; style-src 'unsafe-inline'";

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tasks").RequireAuthorization(AuthSetup.AnalystPolicy);

        g.MapGet("{id:guid}/report.html", async (Guid id, HttpContext ctx, MaldexDbContext db, ReportModelBuilder builder, ReportRenderer renderer, CancellationToken ct) =>
        {
            if (await CheckCompletedAsync(db, id, ct) is { } notReady) return notReady;
            var html = await renderer.RenderHtmlAsync(await builder.BuildAsync(id, ct));
            ctx.Response.Headers.ContentSecurityPolicy = ReportCsp;
            ctx.Response.Headers.ContentDisposition = "inline";
            return Results.Content(html, "text/html; charset=utf-8");
        });

        g.MapGet("{id:guid}/report.pdf", async (Guid id, HttpContext ctx, MaldexDbContext db, ReportModelBuilder builder, ReportRenderer renderer,
            PdfRenderer pdf, ILogger<PdfRenderer> log, CancellationToken ct) =>
        {
            if (await CheckCompletedAsync(db, id, ct) is { } notReady) return notReady;
            var html = await renderer.RenderHtmlAsync(await builder.BuildAsync(id, ct));
            byte[] bytes;
            try
            {
                bytes = await pdf.RenderAsync(html, ct);
            }
            catch (PlaywrightException ex)
            {
                log.LogError(ex, "PDF rendering failed for task {TaskId}. Has Chromium been installed (./build.sh setup)?", id);
                return Results.Problem(title: "PDF rendering is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"maldex-{id}.pdf\"";
            return Results.Bytes(bytes, "application/pdf");
        });

        return app;
    }

    /// <summary>Null when the report may be produced; otherwise the 404 or 409 to return.</summary>
    private static async Task<IResult?> CheckCompletedAsync(MaldexDbContext db, Guid id, CancellationToken ct)
    {
        var status = await db.Tasks.AsNoTracking().Where(t => t.Id == id).Select(t => (RunStatus?)t.Status).FirstOrDefaultAsync(ct);
        if (status is null) return Results.NotFound();
        return status == RunStatus.Completed
            ? null
            : Results.Problem(title: "The report is only available once the task has completed.", statusCode: StatusCodes.Status409Conflict);
    }
}

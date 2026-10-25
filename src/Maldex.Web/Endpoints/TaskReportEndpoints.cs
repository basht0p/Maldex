using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Web.Audit;
using Maldex.Web.Auth;
using Maldex.Web.Reports;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Web.Endpoints;

/// <summary>The JSON task report and the HTTP body download. Not org-scoped (one analyst team).</summary>
public static class TaskReportEndpoints
{
    public static IEndpointRouteBuilder MapTaskReportEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tasks").RequireAuthorization(AuthSetup.AnalystPolicy);

        g.MapGet("{id:guid}/report", async (Guid id, MaldexDbContext db, TaskReportBuilder builder, CancellationToken ct) =>
        {
            var status = await db.Tasks.AsNoTracking().Where(t => t.Id == id).Select(t => (RunStatus?)t.Status).FirstOrDefaultAsync(ct);
            if (status is null) return Results.NotFound();
            if (status != RunStatus.Completed)
                return Results.Problem(title: "The report is only available once the task has completed.", statusCode: StatusCodes.Status409Conflict, type: "report-not-ready");
            var report = await builder.BuildAsync(id, ct);
            return report is null ? Results.NotFound() : Results.Ok(report);
        });

        g.MapGet("{id:guid}/http/{txId:long}/body", async (Guid id, long txId, HttpContext ctx, MaldexDbContext db, IArtifactStore store, AuditWriter audit, CancellationToken ct) =>
        {
            var key = await db.HttpTransactions.AsNoTracking().Where(h => h.Id == txId && h.TaskId == id).Select(h => h.BodyBlobKey).FirstOrDefaultAsync(ct);
            if (key is null || !store.Exists(key)) return Results.NotFound();
            var name = $"http-{txId}";
            var zip = await ArtifactEndpoints.ZipAsync(store, key, name, ct);
            await audit.WriteAsync("artifact.download", $"http:{id}:{txId}", $"task={id}; kind=HttpBody; tx={txId}", ct);
            return ArtifactEndpoints.ZipResult(ctx, zip, name);
        });

        return app;
    }
}

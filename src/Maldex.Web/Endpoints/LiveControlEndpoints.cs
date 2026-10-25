using System.Globalization;
using System.Text;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Core.Live;
using Maldex.Data;
using Maldex.Web.Audit;
using Maldex.Web.Auth;
using Maldex.Web.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Maldex.Web.Endpoints;

public sealed record ExtendRequest(int? Seconds);

/// <summary>
/// Backlog reads for the live task page (the REST half of the rejoin protocol documented on <see cref="Hubs.TaskHub"/>)
/// and the extend/stop controls of a running task.
/// </summary>
public static class LiveControlEndpoints
{
    public const int MaxRunSeconds = 1800;
    public const int MaxEventsPerRequest = 1000;

    public const int MaxExportRows = 100_000;

    private const int MaxSaveAttempts = 5;
    private const int MaxQueryLength = 200;

    private static EventType? ParseFilters(string? type, string? q, Dictionary<string, string[]> errors, out string? text)
    {
        EventType? typeFilter = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (TaskEndpoints.TryParseEnum<EventType>(type, out var parsed)) typeFilter = parsed;
            else errors["type"] = ["Unknown event type."];
        }
        text = null;
        if (q is not null)
        {
            var trimmed = q.Trim();
            if (trimmed.Length is < 1 or > MaxQueryLength) errors["q"] = [$"q must be 1 to {MaxQueryLength} characters."];
            else text = trimmed;
        }
        return typeFilter;
    }

    // The default SQL collation is case-insensitive, so Contains needs no ToLower.
    private static IQueryable<BehaviorEvent> Filtered(MaldexDbContext db, Guid taskId, EventType? type, int? pid, long? processId, string? text)
    {
        var query = db.BehaviorEvents.AsNoTracking().Where(e => e.TaskId == taskId);
        if (type is not null) query = query.Where(e => e.Type == type);
        if (pid is { } p) query = query.Where(e => e.Pid == p);
        if (processId is { } n) query = query.Where(e => e.ProcessNodeId == n);
        if (text is not null) query = query.Where(e => e.Summary.Contains(text) || e.Data.Contains(text));
        return query;
    }

    public static IEndpointRouteBuilder MapLiveControlEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tasks").RequireAuthorization(AuthSetup.AnalystPolicy);

        g.MapGet("{id:guid}/processes", async (Guid id, MaldexDbContext db, CancellationToken ct) =>
        {
            if (!await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == id, ct)) return Results.NotFound();
            var nodes = await db.ProcessNodes.AsNoTracking()
                .Where(n => n.TaskId == id)
                .OrderBy(n => n.Id)
                .Select(n => new ProcessNodeDto(n.Id, n.Pid, n.ParentNodeId, n.ImagePath, n.CommandLine, n.User, n.StartedAt, n.EndedAt))
                .ToListAsync(ct);
            return Results.Ok(nodes);
        });

        g.MapGet("{id:guid}/events", async (Guid id, long? afterId, string? type, int? take, int? pid, long? processId, string? q, MaldexDbContext db, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (afterId is < 0) errors["afterId"] = ["afterId must be 0 or more."];
            var typeFilter = ParseFilters(type, q, errors, out var text);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            if (!await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == id, ct)) return Results.NotFound();

            var after = afterId ?? 0;
            var events = await Filtered(db, id, typeFilter, pid, processId, text)
                .Where(e => e.Id > after)
                .OrderBy(e => e.Id)
                .Take(Math.Clamp(take ?? MaxEventsPerRequest, 1, MaxEventsPerRequest))
                .Select(e => new LiveEventDto(e.Id, e.Type, e.At, e.Pid, e.ProcessNodeId, e.Summary, e.Data))
                .ToListAsync(ct);
            return Results.Ok(events);
        });

        g.MapGet("{id:guid}/events/export", async (Guid id, string? format, string? type, int? pid, long? processId, string? q,
            HttpContext ctx, MaldexDbContext db, AuditWriter audit, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            var fmt = format?.Trim().ToLowerInvariant();
            if (fmt is not ("csv" or "json")) errors["format"] = ["format must be csv or json."];
            var typeFilter = ParseFilters(type, q, errors, out var text);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            if (!await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == id, ct)) return Results.NotFound();

            var json = fmt == "json";
            ctx.Response.ContentType = json ? "application/json" : "text/csv; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"task-{id}-events.{fmt}\"";
            var options = ctx.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;

            var rows = Filtered(db, id, typeFilter, pid, processId, text)
                .OrderBy(e => e.Id)
                .Take(MaxExportRows)
                .Select(e => new LiveEventDto(e.Id, e.Type, e.At, e.Pid, e.ProcessNodeId, e.Summary, e.Data))
                .AsAsyncEnumerable();

            var count = 0;
            var complete = false;
            try
            {
                await using (var writer = new StreamWriter(ctx.Response.Body, new UTF8Encoding(false), 16 * 1024, leaveOpen: true))
                {
                    await writer.WriteAsync(json ? "[" : EventCsv.Header + "\r\n");
                    await foreach (var e in rows.WithCancellation(ct))
                    {
                        if (json)
                        {
                            if (count > 0) await writer.WriteAsync(",");
                            await writer.WriteAsync(JsonSerializer.Serialize(e, options));
                        }
                        else
                        {
                            await writer.WriteAsync(string.Join(',',
                                e.Id.ToString(CultureInfo.InvariantCulture), EventCsv.Cell(e.Type.ToString()), e.At.ToString("O", CultureInfo.InvariantCulture),
                                e.Pid.ToString(CultureInfo.InvariantCulture), e.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "",
                                EventCsv.Cell(e.Summary), EventCsv.Cell(e.DataJson)) + "\r\n");
                        }
                        count++;
                    }
                    if (json) await writer.WriteAsync("]");
                    await writer.FlushAsync(ct);
                }
                complete = true;
            }
            finally
            {
                // Audited even when the client aborts the download (the request token is cancelled by then).
                await audit.WriteAsync("task.events.export", id.ToString(),
                    $"format={fmt}; rows={count}" + (complete ? "" : "; complete=false"), CancellationToken.None);
            }
            return Results.Empty;
        });

        g.MapPost("{id:guid}/extend", async (Guid id, ExtendRequest? req, MaldexDbContext db, AuditWriter audit, CancellationToken ct) =>
        {
            if (req?.Seconds is not { } seconds || seconds is < 1 or > MaxRunSeconds)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["seconds"] = [$"seconds must be an integer between 1 and {MaxRunSeconds}."] });

            for (var attempt = 1; ; attempt++)
            {
                var t = await db.Tasks.Include(t => t.Org).FirstOrDefaultAsync(t => t.Id == id, ct);
                if (t is null) return Results.NotFound();
                if (t.Status != RunStatus.Running || t.StartedAt is not { } startedAt) return NotRunning("extended");

                var maximum = startedAt.AddSeconds(MaxRunSeconds);
                var current = t.DeadlineAt ?? startedAt.AddSeconds(t.DurationSec);
                if (current >= maximum)
                    return Results.Problem(title: $"The deadline is already at its maximum ({MaxRunSeconds} s after the start).", statusCode: StatusCodes.Status400BadRequest);

                var next = current.AddSeconds(seconds);
                if (next > maximum) next = maximum;
                t.DeadlineAt = next;
                try
                {
                    await audit.WriteAsync("task.extend", t.Id.ToString(), $"seconds={seconds}; deadline={next:O}", ct);
                    return Results.Ok(TaskDto.From(t, t.Org!.Name, 0, await TemplateProvenance.LoadAsync(db, t.TemplateVersionId, ct)));
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
                {
                    // The run coordinator wrote the row (for example it just ended the run): re-read and decide again.
                    db.ChangeTracker.Clear();
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Results.Problem(title: "The task is changing; try again.", statusCode: StatusCodes.Status409Conflict);
                }
            }
        });

        g.MapPost("{id:guid}/stop", async (Guid id, MaldexDbContext db, AuditWriter audit, TimeProvider time, CancellationToken ct) =>
        {
            for (var attempt = 1; ; attempt++)
            {
                var t = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct);
                if (t is null) return Results.NotFound();
                if (t.Status is not (RunStatus.Preparing or RunStatus.Running)) return NotRunning("stopped");
                if (t.StopRequestedAt is not null) return Results.Accepted(); // already asked; the coordinator acts within a second

                t.StopRequestedAt = time.GetUtcNow();
                try
                {
                    await audit.WriteAsync("task.stop", t.Id.ToString(), null, ct);
                    return Results.Accepted();
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
                {
                    db.ChangeTracker.Clear();
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Results.Problem(title: "The task is changing; try again.", statusCode: StatusCodes.Status409Conflict);
                }
            }
        });

        return app;
    }

    private static IResult NotRunning(string what) =>
        Results.Problem(title: $"Only a running task can be {what}.", statusCode: StatusCodes.Status409Conflict);
}

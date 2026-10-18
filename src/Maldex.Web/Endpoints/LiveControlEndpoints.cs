using Maldex.Core.Domain;
using Maldex.Core.Live;
using Maldex.Data;
using Maldex.Web.Audit;
using Maldex.Web.Auth;
using Microsoft.EntityFrameworkCore;

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

    private const int MaxSaveAttempts = 5;

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

        g.MapGet("{id:guid}/events", async (Guid id, long? afterId, string? type, int? take, MaldexDbContext db, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (afterId is < 0) errors["afterId"] = ["afterId must be 0 or more."];
            EventType? typeFilter = null;
            if (!string.IsNullOrWhiteSpace(type))
            {
                if (TaskEndpoints.TryParseEnum<EventType>(type, out var parsed)) typeFilter = parsed;
                else errors["type"] = ["Unknown event type."];
            }
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            if (!await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == id, ct)) return Results.NotFound();

            var after = afterId ?? 0;
            var query = db.BehaviorEvents.AsNoTracking().Where(e => e.TaskId == id && e.Id > after);
            if (typeFilter is not null) query = query.Where(e => e.Type == typeFilter);
            var events = await query
                .OrderBy(e => e.Id)
                .Take(Math.Clamp(take ?? MaxEventsPerRequest, 1, MaxEventsPerRequest))
                .Select(e => new LiveEventDto(e.Id, e.Type, e.At, e.Pid, e.ProcessNodeId, e.Summary, e.Data))
                .ToListAsync(ct);
            return Results.Ok(events);
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
                    return Results.Ok(TaskDto.From(t, t.Org!.Name, 0));
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

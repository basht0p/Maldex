using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Lifecycle;
using Maldex.Data;
using Maldex.Data.Templates;
using Maldex.Infrastructure.Guacamole;
using Maldex.Infrastructure.Libvirt;
using Maldex.Orchestrator.Templates;
using Maldex.Web.Auth;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Maldex.Web.Endpoints;

public sealed record TemplateSummaryDto(string Id, string DisplayName, bool IsDefault);

public sealed record TemplateVersionDto(Guid Id, int Number, string Origin, string Status, bool IsCurrent, bool DiskPresent,
    string? WindowsEdition, string? WindowsBuild, bool? OfficePresent, string? AgentVersion, string? SealedBy, DateTimeOffset SealedAt,
    string? Note, long? SizeBytes, string Role);

public sealed record AdminTemplateDto(string Id, string DisplayName, string? Description, bool IsDefault, bool Enabled,
    TemplateVersionDto? Current, IReadOnlyList<TemplateVersionDto> Versions);

public sealed record SetEnabledRequest(bool Enabled);

/// <summary><c>Network</c> is <c>None</c> (default) or <c>Direct</c>; <c>PublishOnSuccess</c> defaults to true.</summary>
public sealed record StartEditRequest(string? Network, bool? PublishOnSuccess);

public sealed record StartRefreshRequest(bool? PublishOnSuccess);

public sealed record SealRequest(string? Note);

public static class TemplateEndpoints
{
    public static IEndpointRouteBuilder MapTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/templates");

        g.MapGet("", async (TemplateCatalog catalog, CancellationToken ct) =>
            (await catalog.AvailableAsync(ct)).Select(t => new TemplateSummaryDto(t.Id, t.DisplayName, t.IsDefault)).ToList())
            .RequireAuthorization(AuthSetup.AnalystPolicy);

        var admin = app.MapGroup("/api/admin/templates").RequireAuthorization(AuthSetup.AdminPolicy);

        admin.MapGet("", async (MaldexDbContext db, TemplateCatalog catalog, CancellationToken ct) =>
            await LoadAsync(db, catalog, null, ct));

        // Each change and its audit row are saved together by the catalog; a no-op change writes no row.
        admin.MapPost("{id}/default", async (string id, MaldexDbContext db, TemplateCatalog catalog, CurrentUser user, CancellationToken ct) =>
        {
            if (await CanonicalIdAsync(db, id, ct) is not { } templateId) return Results.NotFound();
            return await ConflictOr(async () =>
            {
                await catalog.SetDefaultAsync(templateId, ct, new TemplateAudit(ActorOf(user), "template.default"));
                return Results.NoContent();
            });
        });

        admin.MapPost("{id}/enabled", async (string id, SetEnabledRequest req, MaldexDbContext db, TemplateCatalog catalog, CurrentUser user, CancellationToken ct) =>
        {
            if (await CanonicalIdAsync(db, id, ct) is not { } templateId) return Results.NotFound();
            return await ConflictOr(async () =>
            {
                await catalog.SetEnabledAsync(templateId, req.Enabled, ct,
                    new TemplateAudit(ActorOf(user), req.Enabled ? "template.enable" : "template.disable"));
                return Results.NoContent();
            });
        });

        admin.MapPost("{id}/rollback", async (string id, MaldexDbContext db, TemplateCatalog catalog, CurrentUser user, CancellationToken ct) =>
        {
            if (await CanonicalIdAsync(db, id, ct) is not { } templateId) return Results.NotFound();
            var refused = await ConflictOr(async () =>
            {
                await catalog.RollBackAsync(templateId, ct, r => new TemplateAudit(ActorOf(user), "template.rollback", $"from=v{r.From}; to=v{r.To}"));
                return null;
            });
            // Outside ConflictOr: once the rollback is committed, a failure here is a 500, never a refusal.
            return refused ?? Results.Ok((await LoadAsync(db, catalog, templateId, ct)).Single());
        });

        // Held versions (spec §2): sealed by a job that did not publish. Only a Pending version can be published or discarded.
        admin.MapPost("{id}/versions/{versionId:guid}/publish", async (string id, Guid versionId, MaldexDbContext db, TemplateCatalog catalog, CurrentUser user, CancellationToken ct) =>
        {
            if (await CanonicalIdAsync(db, id, ct) is not { } templateId) return Results.NotFound();
            if (await VersionNumberAsync(db, templateId, versionId, ct) is not { } number) return Results.NotFound();
            var refused = await ConflictOr(async () =>
            {
                await catalog.PublishPendingAsync(templateId, versionId, ct, new TemplateAudit(ActorOf(user), "template.publish", $"to=v{number}"));
                return null;
            });
            return refused ?? Results.Ok((await LoadAsync(db, catalog, templateId, ct)).Single());
        });

        admin.MapPost("{id}/versions/{versionId:guid}/discard", async (string id, Guid versionId, MaldexDbContext db, TemplateCatalog catalog, CurrentUser user, CancellationToken ct) =>
        {
            if (await CanonicalIdAsync(db, id, ct) is not { } templateId) return Results.NotFound();
            if (await VersionNumberAsync(db, templateId, versionId, ct) is not { } number) return Results.NotFound();
            var refused = await ConflictOr(async () =>
            {
                await catalog.DiscardPendingAsync(templateId, versionId, ct, new TemplateAudit(ActorOf(user), "template.version.discard", $"version=v{number}"));
                return null;
            });
            return refused ?? Results.Ok((await LoadAsync(db, catalog, templateId, ct)).Single());
        });

        // Edit and Refresh jobs. The endpoints only record the request; TemplateJobWorker starts the job and the coordinator runs it.
        admin.MapPost("{id}/edit", async (string id, StartEditRequest? req, MaldexDbContext db, TemplateCatalog catalog, CurrentUser user,
            ITemplateStore store, IDiskSpaceProbe disk, IOptions<LibvirtOptions> libvirt, IOptions<TemplateJobOptions> jobs, TimeProvider time, CancellationToken ct) =>
        {
            NetworkMode network;
            if (req?.Network is null) network = NetworkMode.None;
            else if (!Enum.TryParse(req.Network, ignoreCase: true, out network) || network is not (NetworkMode.None or NetworkMode.Direct))
                return Results.Problem(title: "network must be None or Direct", statusCode: StatusCodes.Status400BadRequest);
            return await StartJobAsync(id, TemplateJobKind.Edit, network, req?.PublishOnSuccess ?? true, db, catalog, user, store, disk, libvirt, jobs, time, ct);
        });

        admin.MapPost("{id}/refresh", async (string id, StartRefreshRequest? req, MaldexDbContext db, TemplateCatalog catalog, CurrentUser user,
            ITemplateStore store, IDiskSpaceProbe disk, IOptions<LibvirtOptions> libvirt, IOptions<TemplateJobOptions> jobs, TimeProvider time, CancellationToken ct) =>
            await StartJobAsync(id, TemplateJobKind.Refresh, NetworkMode.None, req?.PublishOnSuccess ?? true, db, catalog, user, store, disk, libvirt, jobs, time, ct));

        admin.MapGet("{id}/jobs", async (string id, MaldexDbContext db, IOptions<TemplateJobOptions> jobs, CancellationToken ct) =>
        {
            if (await CanonicalIdAsync(db, id, ct) is not { } templateId) return Results.NotFound();
            var rows = await db.TemplateJobs.AsNoTracking().Where(j => j.TemplateId == templateId)
                .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id).Take(RecentJobs).ToListAsync(ct);
            return Results.Ok(await JobDtosAsync(db, rows, jobs.Value, ct));
        });

        var jobGroup = app.MapGroup("/api/admin/template-jobs").RequireAuthorization(AuthSetup.AdminPolicy);

        jobGroup.MapGet("{jobId:guid}", async (Guid jobId, MaldexDbContext db, IOptions<TemplateJobOptions> jobs, CancellationToken ct) =>
        {
            var job = await db.TemplateJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, ct);
            return job is null ? Results.NotFound() : Results.Ok((await JobDtosAsync(db, [job], jobs.Value, ct)).Single());
        });

        // Both requests are single-column conditional updates (no row-version check): the coordinator writes the same row, and
        // a request must never collide with its progress. The coordinator polls for them.
        jobGroup.MapPost("{jobId:guid}/seal", async (Guid jobId, SealRequest? req, MaldexDbContext db, CurrentUser user, JobConsoleRegistry consoles, TimeProvider time, CancellationToken ct) =>
        {
            var note = string.IsNullOrWhiteSpace(req?.Note) ? null : req!.Note!.Trim();
            if (note is { Length: > MaxNoteLength })
                return Results.Problem(title: $"The note may be at most {MaxNoteLength} characters.", statusCode: StatusCodes.Status400BadRequest);
            var now = time.GetUtcNow();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rows = await db.TemplateJobs
                .Where(j => j.Id == jobId && j.State == TemplateJobState.Configuring && j.SealRequestedAt == null && j.CancelRequestedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.SealRequestedAt, now).SetProperty(j => j.Note, note), ct);
            if (rows == 1)
            {
                db.AuditLogs.Add(await JobAuditAsync(db, jobId, ActorOf(user), "template.job.seal", note is null ? "" : "; with a note", time, ct));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                consoles.Close(jobId); // no console input after the seal decision (R22); the coordinator also waits for them to go
                return Results.Accepted();
            }
            await tx.RollbackAsync(ct);

            var current = await db.TemplateJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => new { j.State, j.SealRequestedAt }).SingleOrDefaultAsync(ct);
            if (current is null) return Results.NotFound();
            if (current is { State: TemplateJobState.Configuring, SealRequestedAt: not null }) return Results.Accepted(); // asked twice
            return Results.Problem(title: current.State == TemplateJobState.Configuring ? "The job is being cancelled." : $"The job is {current.State}, not Configuring.",
                statusCode: StatusCodes.Status409Conflict);
        });

        jobGroup.MapPost("{jobId:guid}/cancel", async (Guid jobId, MaldexDbContext db, CurrentUser user, JobConsoleRegistry consoles, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rows = await db.TemplateJobs
                .Where(j => j.Id == jobId && j.CancelRequestedAt == null && ActiveStates.Contains(j.State))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.CancelRequestedAt, now), ct);
            if (rows == 1)
            {
                // The coordinator writes no cancel row for an admin's cancel (R17); this one names the admin who asked.
                db.AuditLogs.Add(await JobAuditAsync(db, jobId, ActorOf(user), "template.job.cancel", "", time, ct));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                consoles.Close(jobId); // the guest is about to be destroyed: no more console input
                return Results.Accepted();
            }
            await tx.RollbackAsync(ct);

            var current = await db.TemplateJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => new { j.State, j.CancelRequestedAt }).SingleOrDefaultAsync(ct);
            if (current is null) return Results.NotFound();
            if (current.CancelRequestedAt is not null && ActiveStates.Contains(current.State)) return Results.Accepted(); // asked twice
            return Results.Problem(title: $"The job is already {current.State}.", statusCode: StatusCodes.Status409Conflict);
        });

        // Same token flow as the task live view (60 s, single use), bound to the job and the calling admin. The tunnel route is in
        // GuacamoleTunnelEndpoint.
        jobGroup.MapGet("{jobId:guid}/tunnel-token", async (Guid jobId, MaldexDbContext db, CurrentUser user, GuacamoleTokenService tokens, TimeProvider time, CancellationToken ct) =>
        {
            if (!await db.TemplateJobs.AsNoTracking().AnyAsync(j => j.Id == jobId, ct)) return Results.NotFound();
            // Not once a seal or cancel is requested either (R22): the guest is about to be sealed or destroyed.
            if (!await db.TemplateJobs.AsNoTracking().Where(j => j.Id == jobId).Where(TemplateJob.ConsoleOpen).AnyAsync(ct))
                return Results.Problem(title: "The console is only available while the job is configuring and not being sealed or cancelled.", statusCode: StatusCodes.Status409Conflict);

            var token = tokens.Issue(TunnelTokenKind.TemplateJob, jobId, user.UserId);
            db.AuditLogs.Add(await JobAuditAsync(db, jobId, ActorOf(user), "template.console.token", "", time, ct)); // never log the token itself
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { token });
        });

        return app;
    }

    private const int RecentJobs = 20, MaxNoteLength = 1000;

    private static readonly IReadOnlyList<TemplateJobState> ActiveStates = TemplateJobStateMachine.ActiveStates;


    private const int SqlDuplicateKeyIndex = 2601, SqlDuplicateKeyConstraint = 2627;

    /// <summary>
    /// Records an Edit or Refresh job of the template's current version, or refuses (409 with the reason) when one is active,
    /// the base disk is missing or free space is short (<see cref="TemplateCoordinator.DiskSpaceRefusalAsync"/>). The coordinator re-checks all of
    /// it when it starts the job; the unique index <c>IX_TemplateJobs_TemplateId_Active</c> is the backstop for two racing requests.
    /// </summary>
    private static async Task<IResult> StartJobAsync(string id, TemplateJobKind kind, NetworkMode network, bool publishOnSuccess,
        MaldexDbContext db, TemplateCatalog catalog, CurrentUser user, ITemplateStore store, IDiskSpaceProbe disk, IOptions<LibvirtOptions> libvirt,
        IOptions<TemplateJobOptions> jobs, TimeProvider time, CancellationToken ct)
    {
        if (await CanonicalIdAsync(db, id, ct) is not { } templateId) return Results.NotFound();
        static IResult Refuse(string reason) => Results.Problem(title: reason, statusCode: StatusCodes.Status409Conflict);

        var baseVersion = await (from t in db.Templates.AsNoTracking().Where(t => t.Id == templateId)
                                 join v in db.TemplateVersions.AsNoTracking() on t.CurrentVersionId equals (Guid?)v.Id
                                 select v).SingleOrDefaultAsync(ct);
        if (baseVersion is null) return Refuse("the template has no current version to work from");
        if (baseVersion.Status != TemplateVersionStatus.Ready) return Refuse($"the base version v{baseVersion.Number} is {baseVersion.Status}");
        if (!catalog.DiskPresent(baseVersion.FileStem)) return Refuse(TemplateCoordinator.BaseDiskMissing);
        if (await db.TemplateJobs.AsNoTracking().AnyAsync(j => j.TemplateId == templateId && ActiveStates.Contains(j.State), ct))
            return Refuse(TemplateCoordinator.AlreadyRunning);
        if (await TemplateCoordinator.DiskSpaceRefusalAsync(db, store, disk, libvirt.Value, jobs.Value.MinFreeGiB, baseVersion, Guid.Empty, ct) is { } lowSpace)
            return Refuse(lowSpace);

        var now = time.GetUtcNow();
        var job = new TemplateJob
        {
            Id = Guid.NewGuid(), Kind = kind, TemplateId = templateId, BaseVersionId = baseVersion.Id, Network = network,
            RequestedByUserId = user.UserId, CreatedAt = now, PublishOnSuccess = publishOnSuccess,
        };
        db.TemplateJobs.Add(job);
        db.AuditLogs.Add(new AuditLog
        {
            At = now, UserId = user.UserId, Action = "template.job.start", Target = templateId,
            Details = $"job={job.Id}; kind={kind}; network={network}; base=v{baseVersion.Number}; publish={publishOnSuccess}",
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: SqlDuplicateKeyIndex or SqlDuplicateKeyConstraint })
        {
            return Refuse(TemplateCoordinator.AlreadyRunning); // a racing request got its job in first
        }
        var dto = TemplateJobDto.From(job, baseVersion.Number, null, jobs.Value.ConfigureIdleTimeout);
        return Results.Accepted($"/api/admin/template-jobs/{job.Id}", dto);
    }

    /// <summary>The audit row of a job action: the template's id as target, the job in the details.</summary>
    private static async Task<AuditLog> JobAuditAsync(MaldexDbContext db, Guid jobId, Guid? userId, string action, string extra, TimeProvider time, CancellationToken ct) =>
        new()
        {
            At = time.GetUtcNow(), UserId = userId, Action = action, Details = $"job={jobId}{extra}",
            Target = await db.TemplateJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.TemplateId).SingleAsync(ct),
        };

    internal static async Task<List<TemplateJobDto>> JobDtosAsync(MaldexDbContext db, IReadOnlyList<TemplateJob> jobs, TemplateJobOptions options, CancellationToken ct)
    {
        var versionIds = jobs.Where(j => j.BaseVersionId != null).Select(j => j.BaseVersionId!.Value).Concat(jobs.Where(j => j.ResultVersionId != null).Select(j => j.ResultVersionId!.Value)).Distinct().ToList();
        var numbers = await db.TemplateVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Number, ct);
        return jobs.Select(j => TemplateJobDto.From(j, j.BaseVersionId is { } b ? numbers.GetValueOrDefault(b) : 0,
            j.ResultVersionId is { } r ? numbers.GetValueOrDefault(r) : null, options.ConfigureIdleTimeout)).ToList();
    }

    private static Task<int?> VersionNumberAsync(MaldexDbContext db, string templateId, Guid versionId, CancellationToken ct) =>
        db.TemplateVersions.AsNoTracking().Where(v => v.TemplateId == templateId && v.Id == versionId).Select(v => (int?)v.Number).SingleOrDefaultAsync(ct);

    private static Guid? ActorOf(CurrentUser user) => user.IsResolved ? user.UserId : null;

    /// <summary>A change the catalog refused (<see cref="TemplateRuleException"/>) is a 409 titled with its message; nothing else is.</summary>
    private static async Task<IResult?> ConflictOr(Func<Task<IResult?>> action)
    {
        try
        {
            return await action();
        }
        catch (TemplateRuleException ex)
        {
            return Results.Problem(title: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>The stored id for a (case-insensitive) match, or null when no such template exists.</summary>
    private static Task<string?> CanonicalIdAsync(MaldexDbContext db, string id, CancellationToken ct) =>
        db.Templates.AsNoTracking().Where(t => t.Id == id).Select(t => (string?)t.Id).SingleOrDefaultAsync(ct);

    private static async Task<List<AdminTemplateDto>> LoadAsync(MaldexDbContext db, TemplateCatalog catalog, string? onlyId, CancellationToken ct)
    {
        var templates = await db.Templates.AsNoTracking().Where(t => onlyId == null || t.Id == onlyId)
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.DisplayName).ToListAsync(ct);
        var ids = templates.Select(t => t.Id).ToList();
        var versions = await db.TemplateVersions.AsNoTracking().Where(v => ids.Contains(v.TemplateId))
            .OrderByDescending(v => v.Number).ToListAsync(ct);
        var userIds = versions.Where(v => v.SealedByUserId != null).Select(v => v.SealedByUserId!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        return templates.Select(t =>
        {
            var mine = versions.Where(v => v.TemplateId == t.Id).ToList();
            var roles = TemplateCatalog.RolesOf(mine, t.CurrentVersionId);
            var dtos = mine.Select(v => new TemplateVersionDto(
                v.Id, v.Number, v.Origin.ToString(), v.Status.ToString(), v.Id == t.CurrentVersionId,
                v.Status != TemplateVersionStatus.Deleted && catalog.DiskPresent(v.FileStem),
                v.WindowsEdition, v.WindowsBuild, v.OfficePresent, v.AgentVersion,
                v.SealedByUserId is { } u && names.TryGetValue(u, out var name) ? name : null,
                v.SealedAt, v.Note, v.SizeBytes, roles[v.Id])).ToList();
            return new AdminTemplateDto(t.Id, t.DisplayName, t.Description, t.IsDefault, t.Enabled, dtos.FirstOrDefault(d => d.IsCurrent), dtos);
        }).ToList();
    }
}

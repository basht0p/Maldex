using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Data.Templates;
using Maldex.Web.Auth;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Web.Endpoints;

public sealed record TemplateSummaryDto(string Id, string DisplayName, bool IsDefault);

public sealed record TemplateVersionDto(Guid Id, int Number, string Origin, string Status, bool IsCurrent, bool DiskPresent,
    string? WindowsEdition, string? WindowsBuild, bool? OfficePresent, string? AgentVersion, string? SealedBy, DateTimeOffset SealedAt,
    string? Note, long? SizeBytes, string Role);

public sealed record AdminTemplateDto(string Id, string DisplayName, string? Description, bool IsDefault, bool Enabled,
    TemplateVersionDto? Current, IReadOnlyList<TemplateVersionDto> Versions);

public sealed record SetEnabledRequest(bool Enabled);

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

        return app;
    }

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

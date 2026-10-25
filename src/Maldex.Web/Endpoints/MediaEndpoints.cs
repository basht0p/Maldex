using System.Globalization;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Core.Lifecycle;
using Maldex.Core.Media;
using Maldex.Data;
using Maldex.Infrastructure.Media;
using Maldex.Orchestrator.Media;
using Maldex.Web.Auth;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Maldex.Web.Endpoints;

public sealed record InstallerImageDto(int Index, string Name, string? Build, string? Architecture, bool Supported, string? UnsupportedReason);

public sealed record MediaFileDto(Guid Id, string Kind, string FileName, long Size, long ReceivedBytes, string? Sha256, string Source,
    string? SourceUrl, string Status, string? FailureReason, IReadOnlyList<InstallerImageDto> Images, int? PreferredImageIndex,
    string? Application, string AddedBy, DateTimeOffset AddedAt, IReadOnlyList<string> UsedBy);

public sealed record StartUploadRequest(string? FileName, long Size, string? Kind, string? Sha256);

public sealed record StartUploadResponse(MediaFileDto Media, long ChunkBytes);

public sealed record FetchMediaRequest(string? Url, string? Kind, string? FileName);

public static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/media").RequireAuthorization(AuthSetup.AdminPolicy);

        g.MapGet("", async (MaldexDbContext db, CancellationToken ct) =>
        {
            var rows = await db.MediaFiles.AsNoTracking().Where(m => m.Status != MediaStatus.Deleted).OrderByDescending(m => m.AddedAt).ToListAsync(ct);
            return Results.Ok(await ToDtosAsync(db, rows, ct));
        });

        g.MapGet("{id:guid}", async (Guid id, MaldexDbContext db, CancellationToken ct) =>
        {
            var row = await db.MediaFiles.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id && m.Status != MediaStatus.Deleted, ct);
            return row is null ? NotFound() : Results.Ok((await ToDtosAsync(db, [row], ct)).Single());
        });

        g.MapPost("uploads", async (StartUploadRequest req, MediaIntake intake, MaldexDbContext db, CurrentUser user, IOptions<MediaOptions> options, CancellationToken ct) =>
        {
            if (!TryParseKind(req.Kind, out var kind)) return KindProblem();
            return await Guarded(async () =>
            {
                var media = await intake.StartUploadAsync(req.FileName ?? "", req.Size, kind, req.Sha256, user.UserId, ct);
                var dto = (await ToDtosAsync(db, [media], ct)).Single();
                return Results.Created($"/api/admin/media/{media.Id}", new StartUploadResponse(dto, options.Value.ChunkBytes));
            });
        });

        g.MapPut("uploads/{id:guid}", async (Guid id, HttpContext ctx, MediaIntake intake, IOptions<MediaOptions> options, CancellationToken ct) =>
        {
            var max = options.Value.MaxChunkBytes;
            if (ctx.Request.ContentLength is not { } length)
                return Results.Problem(title: "Content-Length is required", statusCode: StatusCodes.Status411LengthRequired);
            if (length > max)
                return Results.Problem(title: $"the chunk is larger than the {max} byte limit", statusCode: StatusCodes.Status413PayloadTooLarge);
            if (!long.TryParse(ctx.Request.Headers["Upload-Offset"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                return Results.Problem(title: "Upload-Offset must be a non-negative integer", statusCode: StatusCodes.Status400BadRequest);

            // Kestrel's default body limit (30 MB) can be below a chunk; raise it for this request only, as TaskEndpoints does.
            var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = max;

            return await Guarded(async () =>
            {
                var (received, status) = await intake.AppendChunkAsync(id, offset, ctx.Request.Body, ct);
                return Results.Ok(new { receivedBytes = received, status = status.ToString() });
            });
        });

        g.MapPost("fetch", async (FetchMediaRequest req, MediaIntake intake, MaldexDbContext db, CurrentUser user, CancellationToken ct) =>
        {
            if (!TryParseKind(req.Kind, out var kind)) return KindProblem();
            if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var url))
                return Results.Problem(title: "url must be an absolute http or https URL", statusCode: StatusCodes.Status400BadRequest);
            return await Guarded(async () =>
            {
                var media = await intake.StartFetchAsync(url, kind, req.FileName, user.UserId, ct);
                return Results.Accepted($"/api/admin/media/{media.Id}", (await ToDtosAsync(db, [media], ct)).Single());
            });
        });

        g.MapDelete("{id:guid}", async (Guid id, MediaIntake intake, CurrentUser user, CancellationToken ct) =>
            await Guarded(async () =>
            {
                await intake.DeleteAsync(id, user.UserId, ct);
                return Results.NoContent();
            }));

        return app;
    }

    private static IResult NotFound() => Results.Problem(title: "media file not found", statusCode: StatusCodes.Status404NotFound);

    private static IResult KindProblem() =>
        Results.Problem(title: "kind must be OsInstaller or Application", statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Exact enum name, any case; numeric strings (which Enum.TryParse would accept) are refused.</summary>
    private static bool TryParseKind(string? text, out MediaKind kind)
    {
        kind = default;
        return text is not null && Enum.GetNames<MediaKind>().Any(n => n.Equals(text, StringComparison.OrdinalIgnoreCase))
            && Enum.TryParse(text, ignoreCase: true, out kind);
    }

    private static async Task<IResult> Guarded(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (MediaRuleException ex)
        {
            return Results.Problem(title: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (MediaNotFoundException ex)
        {
            return Results.Problem(title: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (MediaOffsetException ex)
        {
            return Results.Problem(title: ex.Message, statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["receivedBytes"] = ex.ReceivedBytes });
        }
        catch (Exception ex) when (ex is MediaRefusedException or MediaInUseException)
        {
            return Results.Problem(title: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static List<Guid> IdsOf(string json)
    {
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static async Task<List<MediaFileDto>> ToDtosAsync(MaldexDbContext db, IReadOnlyList<MediaFile> rows, CancellationToken ct)
    {
        var userIds = rows.Select(r => r.AddedByUserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var usedBy = rows.ToDictionary(r => r.Id, _ => new List<string>());
        var versions = await db.TemplateVersions.AsNoTracking().Where(v => v.SourceMediaIds != "[]")
            .Select(v => new { v.TemplateId, v.Number, v.SourceMediaIds }).ToListAsync(ct);
        var jobs = await db.TemplateJobs.AsNoTracking()
            .Where(j => TemplateJobStateMachine.ActiveStates.Contains(j.State) && j.MediaIds != "[]")
            .Select(j => new { j.Id, j.MediaIds }).ToListAsync(ct);
        var templateNames = await db.Templates.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.DisplayName, ct);
        foreach (var v in versions)
            foreach (var id in IdsOf(v.SourceMediaIds).Distinct())
                if (usedBy.TryGetValue(id, out var list)) list.Add($"{templateNames.GetValueOrDefault(v.TemplateId, v.TemplateId)} v{v.Number}");
        foreach (var j in jobs)
            foreach (var id in IdsOf(j.MediaIds).Distinct())
                if (usedBy.TryGetValue(id, out var list)) list.Add($"job {j.Id}");

        return rows.Select(r =>
        {
            var contents = MediaContentsJson.Parse(r.Contents);
            var images = contents?.Images.Select(i =>
            {
                var reason = EditionSelector.Unsupported(i);
                return new InstallerImageDto(i.Index, i.Name, i.Build, i.Architecture, reason is null, reason);
            }).ToList() ?? [];
            return new MediaFileDto(r.Id, r.Kind.ToString(), r.FileName, r.Size, r.ReceivedBytes, r.Sha256, r.Source.ToString(), r.SourceUrl,
                r.Status.ToString(), r.FailureReason, images, contents is null ? null : EditionSelector.Preferred(contents.Images)?.Index,
                contents?.Application, names.GetValueOrDefault(r.AddedByUserId, ""), r.AddedAt, usedBy[r.Id]);
        }).ToList();
    }
}

using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Infrastructure.Storage;
using Maldex.Web.Audit;
using Maldex.Web.Auth;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Web.Endpoints;

public sealed record ArtifactDto(Guid Id, ArtifactKind Kind, string Sha256, long Size);

/// <summary>Artifact listing and downloads. Every download is an AES-256 ZIP (password <see cref="ZipPassword"/>) so nothing live reaches an analyst's disk or AV unwrapped.</summary>
public static class ArtifactEndpoints
{
    public const string ZipPassword = "infected";

    public static IEndpointRouteBuilder MapArtifactEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tasks/{id:guid}/artifacts", async (Guid id, MaldexDbContext db, CancellationToken ct) =>
        {
            if (!await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == id, ct)) return Results.NotFound();
            var list = await db.Artifacts.AsNoTracking().Where(a => a.TaskId == id)
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                .Select(a => new ArtifactDto(a.Id, a.Kind, a.Sha256, a.Size))
                .ToListAsync(ct);
            return Results.Ok(list);
        }).RequireAuthorization(AuthSetup.AnalystPolicy);

        app.MapGet("/api/artifacts/{id:guid}/download", async (Guid id, HttpContext ctx, MaldexDbContext db, IArtifactStore store, AuditWriter audit, CancellationToken ct) =>
        {
            var a = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null || !store.Exists(a.BlobKey)) return Results.NotFound();
            var zip = await ZipAsync(store, a.BlobKey, a.Sha256, ct);
            await audit.WriteAsync("artifact.download", a.Id.ToString(), $"task={a.TaskId}; kind={a.Kind}; sha256={a.Sha256}", ct);
            return ZipResult(ctx, zip, a.Sha256);
        }).RequireAuthorization(AuthSetup.AnalystPolicy);

        // Screenshots only, shown inline in the UI; not audited (a screenshot is not a payload). Sniffed, never trusted.
        app.MapGet("/api/artifacts/{id:guid}/image", async (Guid id, HttpContext ctx, MaldexDbContext db, IArtifactStore store, CancellationToken ct) =>
        {
            var a = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Kind == ArtifactKind.Screenshot, ct);
            if (a is null || !store.Exists(a.BlobKey)) return Results.NotFound();
            await using var source = await store.OpenReadAsync(a.BlobKey, ct);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            var type = bytes is [0x89, 0x50, 0x4E, 0x47, ..] ? "image/png" : bytes is [0xFF, 0xD8, ..] ? "image/jpeg" : null;
            if (type is null) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            ctx.Response.Headers.ContentDisposition = "inline";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.Bytes(bytes, type);
        }).RequireAuthorization(AuthSetup.AnalystPolicy);

        app.MapGet("/api/tasks/{id:guid}/sample", async (Guid id, HttpContext ctx, MaldexDbContext db, IArtifactStore store, AuditWriter audit, CancellationToken ct) =>
        {
            var sample = await db.Tasks.AsNoTracking().Where(t => t.Id == id && t.Sample != null).Select(t => t.Sample).FirstOrDefaultAsync(ct);
            if (sample is null || !store.Exists(sample.BlobKey)) return Results.NotFound();
            var zip = await ZipAsync(store, sample.BlobKey, sample.Sha256, ct);
            await audit.WriteAsync("artifact.download", $"sample:{sample.Sha256}", $"task={id}; kind=Sample; sha256={sample.Sha256}", ct);
            return ZipResult(ctx, zip, sample.Sha256);
        }).RequireAuthorization(AuthSetup.AnalystPolicy);

        return app;
    }

    /// <summary>
    /// Builds the ZIP in memory: ZipOutputStream writes synchronously when it closes, and Kestrel forbids synchronous I/O on the
    /// response body. The entry name is the SHA-256 (a plain leaf, never a path from the guest).
    /// </summary>
    internal static async Task<byte[]> ZipAsync(IArtifactStore store, string blobKey, string sha256, CancellationToken ct)
    {
        await using var source = await store.OpenReadAsync(blobKey, ct);
        using var buffer = new MemoryStream();
        await ProtectedZip.WriteAsync(source, sha256, buffer, ZipPassword, ct);
        return buffer.ToArray();
    }

    internal static IResult ZipResult(HttpContext ctx, byte[] zip, string sha256)
    {
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{sha256}.zip\"";
        return Results.Bytes(zip, "application/zip");
    }
}

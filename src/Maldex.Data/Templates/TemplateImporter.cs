using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Maldex.Data.Templates;

/// <summary>Turns the legacy <c>Maldex:Images</c> ids into v1 templates at startup (idempotent).</summary>
public static class TemplateImporter
{
    public static async Task<int> ImportAsync(MaldexDbContext db, ITemplateStore store, IReadOnlyList<string> legacyImageIds,
        TimeProvider time, ILogger logger, CancellationToken ct)
    {
        var imported = 0;
        foreach (var id in legacyImageIds)
        {
            if (await db.Templates.AnyAsync(t => t.Id == id, ct)) continue;
            if (!store.Exists(id))
            {
                logger.LogWarning("Image {Image} is configured in Maldex:Images but its files do not exist; not imported as a template (build it with ./build.sh build-guest).", id);
                continue;
            }

            var now = time.GetUtcNow();
            var template = new Template
            {
                Id = id, DisplayName = id, Enabled = true, CreatedAt = now,
                IsDefault = !await db.Templates.AnyAsync(t => t.IsDefault, ct),
            };
            var version = new TemplateVersion
            {
                Id = Guid.NewGuid(), TemplateId = id, Number = 1, Origin = TemplateVersionOrigin.Imported, FileStem = id,
                Status = TemplateVersionStatus.Ready, SealedAt = now, SizeBytes = store.SizeOf(id),
            };

            // Template.CurrentVersionId and TemplateVersion.TemplateId reference each other: three saves, one transaction.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Templates.Add(template);
            await db.SaveChangesAsync(ct);
            db.TemplateVersions.Add(version);
            await db.SaveChangesAsync(ct);
            template.CurrentVersionId = version.Id;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogInformation("Imported image {Image} as template {Template} v1.", id, id);
            imported++;
        }
        return imported;
    }
}

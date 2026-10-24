using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
            bool exists;
            try
            {
                exists = store.Exists(id);
            }
            catch (ArgumentException)
            {
                logger.LogWarning("Image {Image} in Maldex:Images is not a valid template id (lowercase letters, digits and '-', at most 64 characters); not imported.", id);
                continue;
            }
            if (!exists)
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

        await EnsureUsableDefaultAsync(db, store, legacyImageIds, time, logger, ct);
        return imported;
    }

    /// <summary>
    /// Fake and real mode can share a database: a fake-mode start imports (and defaults to) an image whose disk real mode
    /// does not have. When the default is not available (<see cref="TemplateCatalog"/>'s rule: enabled, Ready, disk
    /// present) and another template is, the default moves to it: the configured images first, in order, then the rest.
    /// In fake mode every disk exists, so nothing moves.
    /// </summary>
    private static async Task EnsureUsableDefaultAsync(MaldexDbContext db, ITemplateStore store, IReadOnlyList<string> preferred,
        TimeProvider time, ILogger logger, CancellationToken ct)
    {
        var catalog = new TemplateCatalog(db, store, Options.Create(new TemplateOptions()), time);
        if (await catalog.DefaultAsync(ct) is not null) return;
        var available = await catalog.AvailableAsync(ct);
        var next = preferred.Select(id => available.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(t => t is not null) ?? available.FirstOrDefault();
        if (next is null) return;

        var old = await db.Templates.AsNoTracking().Where(t => t.IsDefault).Select(t => t.Id).SingleOrDefaultAsync(ct);
        await catalog.SetDefaultAsync(next.Id, ct);
        logger.LogWarning("The default template {Old} is not available (disabled, no Ready version, or its disk is missing); {New} is now the default.",
            old ?? "(none)", next.Id);
    }
}

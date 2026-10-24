using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maldex.Data.Templates;

/// <summary>A template is disabled, missing, has no current version, or its current version is not Ready.</summary>
public sealed class TemplateUnavailableException(string templateId) : Exception($"template '{templateId}' is not available");

/// <summary>
/// Owns every template rule: lookup, publish, rollback, retention marking, default selection and the disk sweep.
/// Each mutating method saves once (one transaction) and retries on a concurrency conflict, as <see cref="TaskTransitions"/> does.
/// </summary>
public sealed class TemplateCatalog(MaldexDbContext db, IOptions<TemplateOptions> options, TimeProvider time, ILogger<TemplateCatalog>? log = null)
{
    public const int MaxAttempts = 3;

    public async Task<IReadOnlyList<Template>> EnabledAsync(CancellationToken ct) =>
        await db.Templates.AsNoTracking().Where(t => t.Enabled)
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.DisplayName)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <summary>Case-insensitive (database collation); returns the row with its canonical id.</summary>
    public Task<Template?> FindEnabledAsync(string id, CancellationToken ct) =>
        db.Templates.AsNoTracking().Where(t => t.Id == id && t.Enabled).SingleOrDefaultAsync(ct);

    public Task<Template?> DefaultAsync(CancellationToken ct) =>
        db.Templates.AsNoTracking().Where(t => t.IsDefault && t.Enabled).SingleOrDefaultAsync(ct);

    public async Task<TemplateVersion> CurrentReadyVersionAsync(string templateId, CancellationToken ct)
    {
        var template = await FindEnabledAsync(templateId, ct).ConfigureAwait(false);
        if (template?.CurrentVersionId is not { } versionId) throw new TemplateUnavailableException(templateId);
        var version = await db.TemplateVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, ct).ConfigureAwait(false);
        if (version is not { Status: TemplateVersionStatus.Ready }) throw new TemplateUnavailableException(templateId);
        return version;
    }

    /// <summary>Makes <paramref name="versionId"/> current (it must be Ready and belong to the template), then applies retention.</summary>
    public Task PublishAsync(string templateId, Guid versionId, CancellationToken ct) =>
        MutateAsync(async () =>
        {
            var template = await GetTemplateAsync(templateId, ct).ConfigureAwait(false);
            await PublishCoreAsync(template, versionId, ct).ConfigureAwait(false);
        }, ct);

    /// <summary>Publishes the highest-numbered Ready version that is not current.</summary>
    public Task RollBackAsync(string templateId, CancellationToken ct) =>
        MutateAsync(async () =>
        {
            var template = await GetTemplateAsync(templateId, ct).ConfigureAwait(false);
            var previous = await db.TemplateVersions
                .Where(v => v.TemplateId == template.Id && v.Status == TemplateVersionStatus.Ready && v.Id != template.CurrentVersionId)
                .OrderByDescending(v => v.Number)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"template '{template.Id}' has no previous version to roll back to");
            await PublishCoreAsync(template, previous.Id, ct).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// Makes the template the default and clears the old default in one transaction. The old default is saved cleared
    /// first because the unique index is checked per statement, not per transaction.
    /// </summary>
    public Task SetDefaultAsync(string templateId, CancellationToken ct) =>
        RetryAsync(async () =>
        {
            var template = await GetTemplateAsync(templateId, ct).ConfigureAwait(false);
            if (!template.Enabled) throw new InvalidOperationException($"template '{template.Id}' is disabled");
            if (template.IsDefault) return;
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var old in await db.Templates.Where(t => t.IsDefault && t.Id != template.Id).ToListAsync(ct).ConfigureAwait(false))
                old.IsDefault = false;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            template.IsDefault = true;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }, ct);

    public Task SetEnabledAsync(string templateId, bool enabled, CancellationToken ct) =>
        MutateAsync(async () =>
        {
            var template = await GetTemplateAsync(templateId, ct).ConfigureAwait(false);
            if (!enabled && template.IsDefault) throw new InvalidOperationException("choose another default first");
            template.Enabled = enabled;
        }, ct);

    /// <summary>
    /// Deletes the disk of every Retired version that no Preparing or Running task uses, and marks it Deleted.
    /// Each version is recorded on its own (delete, mark, save); a failing delete is logged and the others carry on.
    /// Never touches Ready versions. Returns the versions deleted.
    /// </summary>
    public async Task<IReadOnlyList<TemplateVersion>> SweepAsync(ITemplateStore store, CancellationToken ct)
    {
        List<TemplateVersion> deleted = [];
        var retired = await db.TemplateVersions.Where(v => v.Status == TemplateVersionStatus.Retired).ToListAsync(ct).ConfigureAwait(false);
        foreach (var version in retired)
        {
            try
            {
                var done = false;
                await RetryAsync(async () =>
                {
                    done = false;
                    if (version.Status != TemplateVersionStatus.Retired) return;
                    var inUse = await db.Tasks.AnyAsync(t => t.TemplateVersionId == version.Id
                        && (t.Status == RunStatus.Preparing || t.Status == RunStatus.Running), ct).ConfigureAwait(false);
                    if (inUse) return;
                    await store.DeleteAsync(version.FileStem, ct).ConfigureAwait(false); // idempotent, so safe to repeat on retry
                    version.Status = TemplateVersionStatus.Deleted;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    done = true;
                }, ct).ConfigureAwait(false);
                if (done) deleted.Add(version);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log?.LogError(e, "Sweep of template version {VersionId} ({FileStem}) failed; it stays Retired", version.Id, version.FileStem);
                if (db.Entry(version).State == EntityState.Modified) await db.Entry(version).ReloadAsync(ct).ConfigureAwait(false);
            }
        }
        return deleted;
    }

    private async Task<Template> GetTemplateAsync(string templateId, CancellationToken ct) =>
        await db.Templates.SingleOrDefaultAsync(t => t.Id == templateId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"template '{templateId}' does not exist");

    private async Task PublishCoreAsync(Template template, Guid versionId, CancellationToken ct)
    {
        var versions = await db.TemplateVersions.Where(v => v.TemplateId == template.Id).ToListAsync(ct).ConfigureAwait(false);
        var target = versions.SingleOrDefault(v => v.Id == versionId)
            ?? throw new InvalidOperationException($"version {versionId} does not belong to template '{template.Id}'");
        if (target.Status != TemplateVersionStatus.Ready)
            throw new InvalidOperationException($"version {versionId} is {target.Status}, not Ready");

        template.CurrentVersionId = target.Id;

        // Keep Ready = current + the KeepPrevious highest-numbered other Ready versions; retire the rest.
        var keep = versions
            .Where(v => v.Status == TemplateVersionStatus.Ready && v.Id != target.Id)
            .OrderByDescending(v => v.Number)
            .Take(Math.Max(0, options.Value.KeepPrevious))
            .Select(v => v.Id)
            .ToHashSet();
        foreach (var v in versions.Where(v => v.Status == TemplateVersionStatus.Ready && v.Id != target.Id && !keep.Contains(v.Id)))
            v.Status = TemplateVersionStatus.Retired;
    }

    /// <summary>Runs <paramref name="apply"/> and saves once.</summary>
    private Task MutateAsync(Func<Task> apply, CancellationToken ct) =>
        RetryAsync(async () =>
        {
            await apply().ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }, ct);

    /// <summary>Runs <paramref name="attempt"/>; on a conflict reloads tracked rows and runs it again (max 3 attempts).</summary>
    private async Task RetryAsync(Func<Task> attempt, CancellationToken ct)
    {
        _ = time; // reserved for audit timestamps in later tasks
        for (var n = 1; ; n++)
        {
            try
            {
                await attempt().ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (n < MaxAttempts && IsRetryable(e))
            {
                // A racing writer won (stale row version, or the single-default unique index): drop this attempt and re-read.
                foreach (var entry in db.ChangeTracker.Entries().Where(en => en.Entity is Template or TemplateVersion).ToList())
                {
                    if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
                    else await entry.ReloadAsync(ct).ConfigureAwait(false);
                }
            }
        }
    }

    private const int SqlDuplicateKeyIndex = 2601, SqlDuplicateKeyConstraint = 2627;

    private static bool IsRetryable(Exception e) =>
        e is DbUpdateConcurrencyException
        || (e is DbUpdateException { InnerException: SqlException { Number: SqlDuplicateKeyIndex or SqlDuplicateKeyConstraint } });
}

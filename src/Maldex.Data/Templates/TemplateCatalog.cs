using System.Linq.Expressions;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maldex.Data.Templates;

/// <summary>A template is disabled, missing, has no current version, its current version is not Ready, or its disk is missing.</summary>
public sealed class TemplateUnavailableException(string templateId, string? reason = null)
    : Exception(reason is null ? $"template '{templateId}' is not available" : $"template '{templateId}' is not available: {reason}")
{
    public const string DiskMissing = "its disk is missing";
}

/// <summary>A change the catalog refuses by rule (the API answers 409 with the message as the title).</summary>
public sealed class TemplateRuleException(string message) : InvalidOperationException(message);

/// <summary>
/// The audit row for an admin change: written with the change, in the same transaction, targeting the template id.
/// <see cref="UserId"/> null means the system acted.
/// </summary>
public sealed record TemplateAudit(Guid? UserId, string Action, string? Details = null);

/// <summary>The version numbers a rollback moved between (<see cref="From"/> is null when there was no current version).</summary>
public sealed record RollBackResult(int? From, int To);

/// <summary>
/// Owns every template rule: lookup, publish, rollback, retention marking, default selection and the disk sweep.
/// Each mutating method saves once (one transaction) and retries on a concurrency conflict, as <see cref="TaskTransitions"/> does.
/// A template is <em>available</em> (listed for analysts, accepted on submission, usable as the default) only when it is
/// enabled, its current version is Ready and that version's disk exists in the <see cref="ITemplateStore"/>.
/// </summary>
public sealed class TemplateCatalog(MaldexDbContext db, ITemplateStore store, IOptions<TemplateOptions> options, TimeProvider time, ILogger<TemplateCatalog>? log = null)
{
    public const int MaxAttempts = 3;

    private static readonly IReadOnlyList<TemplateJobState> ActiveJobStates = TemplateJobStateMachine.ActiveStates;

    /// <summary>Every available template, the default first, then by display name.</summary>
    public Task<List<Template>> AvailableAsync(CancellationToken ct) => AvailableWhereAsync(_ => true, ct);

    /// <summary>Case-insensitive (database collation); returns the row with its canonical id, or null when it is not available.</summary>
    public async Task<Template?> FindAvailableAsync(string id, CancellationToken ct) =>
        (await AvailableWhereAsync(t => t.Id == id, ct).ConfigureAwait(false)).SingleOrDefault();

    /// <summary>The default template, or null when it is not available.</summary>
    public async Task<Template?> DefaultAsync(CancellationToken ct) =>
        (await AvailableWhereAsync(t => t.IsDefault, ct).ConfigureAwait(false)).SingleOrDefault();

    /// <summary>The one availability rule: enabled, current version Ready, and that version's disk present.</summary>
    private async Task<List<Template>> AvailableWhereAsync(Expression<Func<Template, bool>> filter, CancellationToken ct)
    {
        var rows = await (
            from t in db.Templates.AsNoTracking().Where(t => t.Enabled).Where(filter)
            join v in db.TemplateVersions.AsNoTracking() on t.CurrentVersionId equals (Guid?)v.Id
            where v.Status == TemplateVersionStatus.Ready
            orderby t.IsDefault descending, t.DisplayName
            select new { Template = t, v.FileStem }).ToListAsync(ct).ConfigureAwait(false);
        return rows.Where(r => DiskPresent(r.FileStem)).Select(r => r.Template).ToList();
    }

    /// <summary>
    /// Whether the version's disk exists. A stem the store rejects as invalid (it throws <see cref="ArgumentException"/>) has
    /// no disk, exactly like a missing one. The one place the catalog and the admin list ask the store.
    /// </summary>
    public bool DiskPresent(string fileStem)
    {
        try
        {
            return store.Exists(fileStem);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The template's current version, when the template is available.</summary>
    /// <exception cref="TemplateUnavailableException">Unknown, disabled, no Ready current version, or its disk is missing.</exception>
    public async Task<TemplateVersion> CurrentReadyVersionAsync(string templateId, CancellationToken ct)
    {
        var template = await db.Templates.AsNoTracking().Where(t => t.Id == templateId && t.Enabled).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (template?.CurrentVersionId is not { } versionId) throw new TemplateUnavailableException(templateId);
        var version = await db.TemplateVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, ct).ConfigureAwait(false);
        if (version is not { Status: TemplateVersionStatus.Ready }) throw new TemplateUnavailableException(templateId);
        if (!DiskPresent(version.FileStem)) throw new TemplateUnavailableException(template.Id, TemplateUnavailableException.DiskMissing);
        return version;
    }

    /// <summary>
    /// Binds the task to its template's current Ready version and saves; a task that is already bound keeps its version.
    /// Runs in a transaction that re-reads the chosen version under UPDLOCK, HOLDLOCK: a publish that retires it must wait
    /// for this commit, and the sweep only deletes a Retired version no Preparing/Running task is bound to, so a disk
    /// can never be deleted between choosing the version and recording the choice. A version found retired after the
    /// lock is taken is resolved once more; if that fails too the task stays unbound.
    /// </summary>
    /// <exception cref="TemplateUnavailableException">No Ready version could be bound.</exception>
    // Not named BindAsync: minimal APIs treat that name on an injected type as a custom parameter binder.
    public async Task<TemplateVersion> BindTaskVersionAsync(AnalysisTask task, CancellationToken ct)
    {
        if (task.TemplateVersionId is { } bound)
        {
            var kept = await db.TemplateVersions.AsNoTracking().SingleAsync(v => v.Id == bound, ct).ConfigureAwait(false);
            if (!DiskPresent(kept.FileStem)) throw new TemplateUnavailableException(kept.TemplateId, TemplateUnavailableException.DiskMissing);
            return kept;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        for (var n = 1; ; n++)
        {
            var current = await CurrentReadyVersionAsync(task.Image, ct).ConfigureAwait(false);
            var locked = await db.TemplateVersions
                .FromSqlInterpolated($"SELECT * FROM TemplateVersions WITH (UPDLOCK, HOLDLOCK) WHERE Id = {current.Id}")
                .AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (locked is { Status: TemplateVersionStatus.Ready })
            {
                // One column, no row-version check: the API may write the task meanwhile (a stop is allowed while
                // Preparing), and that must not turn into a concurrency failure of the run.
                var taskId = task.Id;
                await db.Tasks.Where(t => t.Id == taskId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.TemplateVersionId, locked.Id), ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                // Mirror the saved value on the entity without leaving it marked modified.
                task.TemplateVersionId = locked.Id;
                var entry = db.Entry(task);
                if (entry.State != EntityState.Detached)
                {
                    var column = entry.Property(t => t.TemplateVersionId);
                    column.OriginalValue = locked.Id;
                    column.IsModified = false;
                }
                return locked;
            }
            if (n == 2) throw new TemplateUnavailableException(task.Image);
        }
    }

    /// <summary>Makes <paramref name="versionId"/> current (it must be Ready and belong to the template), then applies retention.</summary>
    /// <exception cref="TemplateRuleException">The version is not Ready or belongs to another template.</exception>
    public Task PublishAsync(string templateId, Guid versionId, CancellationToken ct, TemplateAudit? audit = null) =>
        MutateAsync(async () =>
        {
            var template = await GetTemplateAsync(templateId, ct).ConfigureAwait(false);
            await PublishCoreAsync(template, versionId, ct).ConfigureAwait(false);
            AddAudit(audit, template.Id);
        }, ct);

    /// <summary>
    /// Publishes a held version (sealed by a job that was told not to publish, so Ready and never current): as
    /// <see cref="PublishAsync"/>, but any other version is refused.
    /// </summary>
    /// <exception cref="TemplateRuleException">The version is not Pending.</exception>
    public Task PublishPendingAsync(string templateId, Guid versionId, CancellationToken ct, TemplateAudit? audit = null) =>
        MutateAsync(async () =>
        {
            var template = await GetTemplateAsync(templateId, ct).ConfigureAwait(false);
            await RequirePendingAsync(template, versionId, ct).ConfigureAwait(false);
            await PublishCoreAsync(template, versionId, ct).ConfigureAwait(false);
            AddAudit(audit, template.Id);
        }, ct);

    /// <summary>Retires a held version; the sweep then deletes its disk.</summary>
    /// <exception cref="TemplateRuleException">The version is not Pending.</exception>
    public Task DiscardPendingAsync(string templateId, Guid versionId, CancellationToken ct, TemplateAudit? audit = null) =>
        MutateAsync(async () =>
        {
            var template = await GetTemplateAsync(templateId, ct).ConfigureAwait(false);
            var version = await RequirePendingAsync(template, versionId, ct).ConfigureAwait(false);
            version.Status = TemplateVersionStatus.Retired;
            AddAudit(audit, template.Id);
        }, ct);

    /// <summary>The version, when it is Pending (<see cref="RolesOf"/>: Ready and never current).</summary>
    private async Task<TemplateVersion> RequirePendingAsync(Template template, Guid versionId, CancellationToken ct)
    {
        var versions = await db.TemplateVersions.Where(v => v.TemplateId == template.Id).ToListAsync(ct).ConfigureAwait(false);
        var version = versions.SingleOrDefault(v => v.Id == versionId)
            ?? throw new TemplateRuleException($"version {versionId} does not belong to template '{template.Id}'");
        var role = RolesOf(versions, template.CurrentVersionId)[version.Id];
        if (role != "Pending") throw new TemplateRuleException($"version v{version.Number} is {role}, not Pending");
        return version;
    }

    /// <summary>
    /// Publishes the Ready version that was current most recently (the latest <see cref="TemplateVersion.CurrentUntil"/>),
    /// and retires the version being rolled back from, so a rollback never keeps the bad version. Returns the version
    /// numbers it moved between, as read in the attempt that was saved; <paramref name="audit"/> builds the audit row from them.
    /// </summary>
    /// <exception cref="TemplateRuleException">No Ready version was ever current before the current one.</exception>
    public async Task<RollBackResult> RollBackAsync(string templateId, CancellationToken ct, Func<RollBackResult, TemplateAudit>? audit = null)
    {
        RollBackResult? result = null;
        await MutateAsync(async () =>
        {
            var template = await GetTemplateAsync(templateId, ct).ConfigureAwait(false);
            var versions = await db.TemplateVersions.Where(v => v.TemplateId == template.Id).ToListAsync(ct).ConfigureAwait(false);
            var previous = PreviousVersions(versions, template.CurrentVersionId).FirstOrDefault()
                ?? throw new TemplateRuleException($"template '{template.Id}' has no previous version to roll back to");
            var from = versions.SingleOrDefault(v => v.Id == template.CurrentVersionId);
            result = new RollBackResult(from?.Number, previous.Number);
            // The version rolled back from is never kept: retire it now, before retention counts the others.
            if (from is { Status: TemplateVersionStatus.Ready }) from.Status = TemplateVersionStatus.Retired;
            await PublishCoreAsync(template, previous.Id, ct).ConfigureAwait(false);
            if (audit is not null) AddAudit(audit(result), template.Id);
        }, ct).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Makes the template the default and clears the old default in one transaction. The old default is saved cleared
    /// first because the unique index is checked per statement, not per transaction. Already the default: nothing changes
    /// and nothing is audited. Inside a transaction the caller opened (<c>db.Database.CurrentTransaction</c>), it joins that
    /// transaction and commits (or rolls back) with it.
    /// </summary>
    /// <exception cref="TemplateRuleException">The template is disabled.</exception>
    public Task SetDefaultAsync(string templateId, CancellationToken ct, TemplateAudit? audit = null) =>
        RetryAsync(async () =>
        {
            var template = await GetTemplateAsync(templateId, ct).ConfigureAwait(false);
            if (!template.Enabled) throw new TemplateRuleException($"template '{template.Id}' is disabled");
            if (template.IsDefault) return;
            await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
            foreach (var old in await db.Templates.Where(t => t.IsDefault && t.Id != template.Id).ToListAsync(ct).ConfigureAwait(false))
                old.IsDefault = false;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            template.IsDefault = true;
            AddAudit(audit, template.Id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (tx is not null) await tx.CommitAsync(ct).ConfigureAwait(false);
        }, ct);

    /// <summary>Enables or disables the template. Already in that state: nothing changes and nothing is audited.</summary>
    /// <exception cref="TemplateRuleException">Disabling the default.</exception>
    public Task SetEnabledAsync(string templateId, bool enabled, CancellationToken ct, TemplateAudit? audit = null) =>
        MutateAsync(async () =>
        {
            var template = await GetTemplateAsync(templateId, ct).ConfigureAwait(false);
            if (!enabled && template.IsDefault) throw new TemplateRuleException("choose another default first");
            if (template.Enabled == enabled) return;
            template.Enabled = enabled;
            AddAudit(audit, template.Id);
        }, ct);

    /// <summary>
    /// Deletes the disk of every Retired version that no Preparing, Running or Collecting task uses and that is not the base
    /// of a non-terminal template job, and marks it Deleted.
    /// Each version is recorded on its own (delete, then mark it and add its system audit row in one save); a failing
    /// version is logged, stays Retired, and the others carry on. Never touches Ready versions. Returns the versions deleted.
    /// </summary>
    public async Task<IReadOnlyList<TemplateVersion>> SweepAsync(CancellationToken ct)
    {
        List<TemplateVersion> deleted = [];
        var retired = await db.TemplateVersions.Where(v => v.Status == TemplateVersionStatus.Retired)
            .OrderBy(v => v.TemplateId).ThenBy(v => v.Number).ToListAsync(ct).ConfigureAwait(false);
        foreach (var version in retired)
        {
            try
            {
                var done = false;
                await RetryAsync(async () =>
                {
                    done = false;
                    if (version.Status != TemplateVersionStatus.Retired) return;
                    // Collecting too: the VM is still alive while dropped files are collected.
                    var inUse = await db.Tasks.AnyAsync(t => t.TemplateVersionId == version.Id
                        && (t.Status == RunStatus.Preparing || t.Status == RunStatus.Running || t.Status == RunStatus.Collecting), ct).ConfigureAwait(false);
                    if (inUse) return;
                    // A job still working from it may not have copied it yet (a rollback can retire a running job's base).
                    var jobBase = await db.TemplateJobs.AnyAsync(j => j.BaseVersionId == version.Id && ActiveJobStates.Contains(j.State), ct).ConfigureAwait(false);
                    if (jobBase) return;
                    await store.DeleteAsync(version.FileStem, ct).ConfigureAwait(false); // idempotent, so safe to repeat on retry
                    version.Status = TemplateVersionStatus.Deleted;
                    AddAudit(new TemplateAudit(null, "template.version.delete", $"template={version.TemplateId}; v={version.Number}"), version.Id.ToString());
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    done = true;
                }, ct).ConfigureAwait(false);
                if (done) deleted.Add(version);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log?.LogError(e, "Sweep of template version {VersionId} ({FileStem}) failed; it stays Retired", version.Id, version.FileStem);
                DiscardPendingAudits();
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
            ?? throw new TemplateRuleException($"version {versionId} does not belong to template '{template.Id}'");
        if (target.Status != TemplateVersionStatus.Ready)
            throw new TemplateRuleException($"version {versionId} is {target.Status}, not Ready");

        if (versions.SingleOrDefault(v => v.Id == template.CurrentVersionId) is { } old && old.Id != target.Id)
            old.CurrentUntil = time.GetUtcNow();
        template.CurrentVersionId = target.Id;
        target.CurrentUntil = null;

        // Keep Ready = current + the KeepPrevious versions that were current most recently; a sealed version that was
        // never current (CurrentUntil null) is pending an admin's decision and is never retired here.
        var previous = PreviousVersions(versions, target.Id);
        foreach (var v in previous.Skip(Math.Max(0, options.Value.KeepPrevious)))
            v.Status = TemplateVersionStatus.Retired;
    }

    /// <summary>
    /// The Ready versions that were current before (not the current one), most recently current first. The first is the
    /// rollback target. Ties (same instant) go to the higher number.
    /// </summary>
    private static List<TemplateVersion> PreviousVersions(IEnumerable<TemplateVersion> versions, Guid? currentId) =>
        versions
            .Where(v => v.Status == TemplateVersionStatus.Ready && v.Id != currentId && v.CurrentUntil != null)
            .OrderByDescending(v => v.CurrentUntil).ThenByDescending(v => v.Number)
            .ToList();

    /// <summary>
    /// What each version of a template is for the admin: <c>Current</c>; <c>RollbackTarget</c> (what <see cref="RollBackAsync"/>
    /// would publish); <c>Previous</c> (the other versions that were current before); <c>Pending</c> (Ready, never current);
    /// <c>Retired</c>; <c>Deleted</c>.
    /// </summary>
    public static Dictionary<Guid, string> RolesOf(IReadOnlyCollection<TemplateVersion> versions, Guid? currentId)
    {
        var previous = PreviousVersions(versions, currentId);
        return versions.ToDictionary(v => v.Id, v =>
            v.Id == currentId ? "Current"
            : v.Status == TemplateVersionStatus.Retired ? "Retired"
            : v.Status == TemplateVersionStatus.Deleted ? "Deleted"
            : previous.Count > 0 && previous[0].Id == v.Id ? "RollbackTarget"
            : v.CurrentUntil != null ? "Previous"
            : "Pending");
    }

    /// <summary>Adds the change's audit row to the pending save, so it commits (or fails) together with the change.</summary>
    private void AddAudit(TemplateAudit? audit, string target)
    {
        if (audit is null) return;
        db.AuditLogs.Add(new AuditLog { At = time.GetUtcNow(), UserId = audit.UserId, Action = audit.Action, Target = target, Details = audit.Details });
    }

    /// <summary>Drops audit rows of an attempt that did not save, so a retry (or the next sweep item) does not write them.</summary>
    private void DiscardPendingAudits()
    {
        foreach (var entry in db.ChangeTracker.Entries<AuditLog>().Where(en => en.State == EntityState.Added).ToList())
            entry.State = EntityState.Detached;
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
        for (var n = 1; ; n++)
        {
            try
            {
                await attempt().ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (n < MaxAttempts && IsRetryable(e))
            {
                // A racing writer won (stale row version, or the single-default unique index): drop this attempt, its
                // audit row included, and re-read.
                DiscardPendingAudits();
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

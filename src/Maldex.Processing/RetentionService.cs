using System.Globalization;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Maldex.Processing;

public sealed record RetentionResult(int TasksDeleted, int BlobsDeleted, int SamplesDeleted);

/// <summary>
/// Purges terminal tasks older than their org's <c>RetentionDays</c>, then samples nothing references any more.
/// Task child rows (processes, events, flows, HTTP, detections, history, artifacts) go with the task via the
/// database's cascading foreign keys; artifact and sample blobs are deleted here.
/// </summary>
public sealed class RetentionService(IServiceScopeFactory scopes, IArtifactStore artifacts, ILogger<RetentionService> logger)
{
    private const int BatchSize = 500;

    public async Task<RetentionResult> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();

        int tasks = 0, blobs = 0, samples = 0;

        var orgs = await db.Orgs.AsNoTracking().Select(o => new { o.Id, o.RetentionDays }).ToListAsync(ct).ConfigureAwait(false);
        foreach (var org in orgs)
        {
            var cutoff = now.AddDays(-org.RetentionDays);
            while (true)
            {
                // Cancelled tasks age from submission, Completed/Failed from completion. Non-terminal tasks are never touched.
                var ids = await db.Tasks.AsNoTracking()
                    .Where(t => t.OrgId == org.Id).Where(Expired(cutoff))
                    .OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
                    .Select(t => t.Id)
                    .Take(BatchSize)
                    .ToListAsync(ct).ConfigureAwait(false);
                if (ids.Count == 0) break;

                var keys = await db.Artifacts.AsNoTracking().Where(a => ids.Contains(a.TaskId)).Select(a => a.BlobKey).Distinct()
                    .ToListAsync(ct).ConfigureAwait(false);

                tasks += await db.Tasks.Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);

                // Blobs are content-addressed, so a surviving task's artifact may share the file: keep any key still referenced.
                var stillUsed = keys.Count == 0
                    ? []
                    : await db.Artifacts.AsNoTracking().Where(a => keys.Contains(a.BlobKey)).Select(a => a.BlobKey).Distinct()
                        .ToListAsync(ct).ConfigureAwait(false);
                foreach (var key in keys.Except(stillUsed))
                {
                    await artifacts.DeleteAsync(key, ct).ConfigureAwait(false);
                    blobs++;
                }
            }
        }

        // Only now (against the tasks that remain) is a sample known to be orphaned.
        var orphans = await db.Samples.AsNoTracking()
            .Where(s => !db.Tasks.Any(t => t.SampleSha256 == s.Sha256))
            .Select(s => new { s.Sha256, s.BlobKey })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var orphan in orphans)
        {
            // Re-check in the delete itself so a task submitted since the listing keeps its sample.
            var removed = await db.Samples
                .Where(s => s.Sha256 == orphan.Sha256 && !db.Tasks.Any(t => t.SampleSha256 == s.Sha256))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            if (removed == 0) continue;
            await artifacts.DeleteAsync(orphan.BlobKey, ct).ConfigureAwait(false);
            samples++;
        }

        // The system acted (no user), so the row is written directly rather than through the request-scoped AuditWriter.
        db.AuditLogs.Add(new AuditLog
        {
            At = now,
            UserId = null,
            Action = "retention.purge",
            Target = "retention",
            Details = string.Create(CultureInfo.InvariantCulture, $"tasks={tasks}; blobs={blobs}; samples={samples}"),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Retention purge: {Tasks} tasks, {Blobs} artifact blobs, {Samples} samples deleted.", tasks, blobs, samples);
        return new RetentionResult(tasks, blobs, samples);
    }

    private static System.Linq.Expressions.Expression<Func<AnalysisTask, bool>> Expired(DateTimeOffset cutoff) =>
        t => (t.Status == RunStatus.Cancelled && t.CreatedAt < cutoff)
          || ((t.Status == RunStatus.Completed || t.Status == RunStatus.Failed) && (t.CompletedAt ?? t.CreatedAt) < cutoff);
}

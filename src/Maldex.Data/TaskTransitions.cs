using Maldex.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Data;

/// <summary>
/// The one place that applies a task state-machine transition and saves it. The task row is also written by the
/// API (stop, extend, cancel), so the entity is reloaded first and the save retried on a concurrency conflict.
/// </summary>
public static class TaskTransitions
{
    public const int DefaultMaxAttempts = 5;

    /// <summary>
    /// Reloads <paramref name="task"/>, applies <c>TransitionTo(to, reason)</c> and <paramref name="mutate"/>, and saves.
    /// Returns false (nothing written) when <paramref name="onlyFrom"/> is set and the reloaded task is no longer in that
    /// status. An illegal transition throws, as <see cref="AnalysisTask.TransitionTo"/> does.
    /// </summary>
    public static async Task<bool> TransitionWithRetryAsync(
        MaldexDbContext db, AnalysisTask task, RunStatus to, string reason, DateTimeOffset at,
        Action<AnalysisTask>? mutate, CancellationToken ct, RunStatus? onlyFrom = null, int maxAttempts = DefaultMaxAttempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            await db.Entry(task).ReloadAsync(ct).ConfigureAwait(false);
            if (onlyFrom is { } from && task.Status != from) return false;

            task.TransitionTo(to, reason, at);
            mutate?.Invoke(task);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
            {
                // Drop the history row this attempt added; the next attempt adds it again on the reloaded task.
                foreach (var h in task.StatusHistory.Where(h => db.Entry(h).State == EntityState.Added).ToList())
                {
                    db.Entry(h).State = EntityState.Detached;
                    task.StatusHistory.Remove(h);
                }
            }
        }
    }
}

using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Data.Templates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maldex.Processing;

/// <summary>
/// Runs <see cref="RetentionService"/> five minutes after startup, then every 24 hours; each cycle also sweeps the disks of
/// retired template versions (<see cref="TemplateCatalog.SweepAsync"/>).
/// </summary>
public sealed class RetentionWorker(RetentionService retention, BlobGarbageCollector blobGc, IServiceScopeFactory scopes, TimeProvider time, ILogger<RetentionWorker> logger) : BackgroundService
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>
    /// Deletes the disks of Retired template versions no Preparing/Running task uses and writes one system audit row
    /// (no user) per deleted version.
    /// </summary>
    public async Task SweepTemplatesAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<TemplateCatalog>();
        var store = scope.ServiceProvider.GetRequiredService<ITemplateStore>();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();

        var deleted = await catalog.SweepAsync(store, ct).ConfigureAwait(false);
        if (deleted.Count == 0) return;

        var at = time.GetUtcNow();
        foreach (var v in deleted)
            db.AuditLogs.Add(new AuditLog { At = at, UserId = null, Action = "template.version.delete", Target = v.Id.ToString(), Details = $"template={v.TemplateId}; v={v.Number}" });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, time, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                try
                {
                    await retention.PurgeAsync(time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Retention purge failed; will retry at the next interval.");
                }
                try
                {
                    await blobGc.SweepAsync(time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Blob GC sweep failed; will retry at the next interval.");
                }
                try
                {
                    await SweepTemplatesAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Template version sweep failed; will retry at the next interval.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host shutdown
        }
    }
}

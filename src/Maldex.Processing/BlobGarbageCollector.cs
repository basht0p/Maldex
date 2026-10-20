using System.Globalization;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Maldex.Processing;

public sealed record GcResult(int BlobsDeleted, int TempFilesDeleted);

/// <summary>
/// Deletes blobs that no <c>Artifact</c>, <c>Sample</c> or <c>HttpTransaction</c> row references, plus stale scan/Suricata temp
/// files left by crashes. Anything newer than <c>Maldex:Retention:BlobGcGrace</c> (default 1 h) is spared so in-flight work survives.
/// </summary>
public sealed class BlobGarbageCollector(IServiceScopeFactory scopes, IArtifactStore artifacts, IConfiguration configuration, ILogger<BlobGarbageCollector> logger)
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromHours(1);
    private static readonly string[] TempPrefixes = ["maldex-scan-", "maldex-suricata-"];

    private TimeSpan Grace =>
        TimeSpan.TryParse(configuration["Maldex:Retention:BlobGcGrace"], CultureInfo.InvariantCulture, out var g) && g >= TimeSpan.Zero ? g : DefaultGrace;

    private string TempPath =>
        configuration["Maldex:Retention:TempPath"] is { Length: > 0 } p ? p : Path.GetTempPath();

    public async Task<GcResult> SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - Grace;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();

        // List first, read references second: a blob stored after listing is not a candidate, and one stored before is younger than the cutoff.
        var candidates = new List<string>();
        await foreach (var (key, lastWrite) in artifacts.ListAsync(ct).ConfigureAwait(false))
            if (lastWrite < cutoff) candidates.Add(key);

        var blobs = 0;
        if (candidates.Count > 0)
        {
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            referenced.UnionWith(await db.Artifacts.AsNoTracking().Select(a => a.BlobKey).Distinct().ToListAsync(ct).ConfigureAwait(false));
            referenced.UnionWith(await db.Samples.AsNoTracking().Select(s => s.BlobKey).Distinct().ToListAsync(ct).ConfigureAwait(false));
            referenced.UnionWith(await db.HttpTransactions.AsNoTracking().Where(h => h.BodyBlobKey != null).Select(h => h.BodyBlobKey!).Distinct()
                .ToListAsync(ct).ConfigureAwait(false));
            foreach (var key in candidates.Where(k => !referenced.Contains(k)))
            {
                await artifacts.DeleteAsync(key, ct).ConfigureAwait(false);
                blobs++;
            }
        }

        var temps = SweepTempFiles(cutoff);

        if (blobs + temps > 0)
        {
            db.AuditLogs.Add(new AuditLog
            {
                At = now,
                UserId = null,
                Action = "retention.blob_gc",
                Target = "retention",
                Details = string.Create(CultureInfo.InvariantCulture, $"blobs={blobs}; temp_files={temps}"),
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        logger.LogInformation("Blob GC: {Blobs} unreferenced blobs, {Temp} stale temp entries deleted.", blobs, temps);
        return new GcResult(blobs, temps);
    }

    private int SweepTempFiles(DateTimeOffset cutoff)
    {
        var dir = TempPath;
        if (!Directory.Exists(dir)) return 0;
        var deleted = 0;
        foreach (var prefix in TempPrefixes)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir, prefix + "*"))
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        if (new DateTimeOffset(Directory.GetLastWriteTimeUtc(entry), TimeSpan.Zero) >= cutoff) continue;
                        Directory.Delete(entry, recursive: true);
                    }
                    else
                    {
                        if (new DateTimeOffset(File.GetLastWriteTimeUtc(entry), TimeSpan.Zero) >= cutoff) continue;
                        File.Delete(entry);
                    }
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not delete stale temp entry {Entry}.", entry);
                }
            }
        }
        return deleted;
    }
}

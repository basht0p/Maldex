using System.Text;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Storage;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Integration.Tests.Orchestrator;
using Maldex.Processing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Maldex.Integration.Tests.Processing;

[Collection("sql")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class BlobGcTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> PutAsync(OrchestratorHarness h, string category = "screenshots")
    {
        await using var content = new MemoryStream(Encoding.ASCII.GetBytes("blob " + Guid.NewGuid()));
        return (await h.Get<IArtifactStore>().PutAsync(category, content, 1_048_576, Ct)).Key;
    }

    private static void Age(OrchestratorHarness h, string key, TimeSpan age) =>
        File.SetLastWriteTimeUtc(((FileSystemArtifactStore)h.Get<IArtifactStore>()).GetPath(key), DateTime.UtcNow - age);

    private static Dictionary<string, string?> Settings(string tempDir) => new() { ["Maldex:Retention:TempPath"] = tempDir };

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maldex-gctest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Sweep_DeletesUnreferencedBlob()
    {
        var temp = NewTempDir();
        try
        {
            await using var h = await OrchestratorHarness.CreateAsync(sql, Settings(temp));
            var key = await PutAsync(h);
            Age(h, key, TimeSpan.FromHours(5));

            var result = await h.Get<BlobGarbageCollector>().SweepAsync(DateTimeOffset.UtcNow, Ct);

            Assert.Equal(new GcResult(1, 0), result);
            Assert.False(h.Get<IArtifactStore>().Exists(key));
            await using var db = h.NewContext();
            var audit = await db.AuditLogs.SingleAsync(a => a.Action == "retention.blob_gc", Ct);
            Assert.Null(audit.UserId);
            Assert.Contains("blobs=1", audit.Details);
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task Sweep_KeepsReferencedBlob()
    {
        var temp = NewTempDir();
        try
        {
            await using var h = await OrchestratorHarness.CreateAsync(sql, Settings(temp));
            var taskId = await h.SeedPreparingTaskAsync(sampleContent: Encoding.ASCII.GetBytes("sample " + Guid.NewGuid()));
            var artifactKey = await PutAsync(h);
            var bodyKey = await PutAsync(h, "bodies");
            var orphan = await PutAsync(h);
            string sampleKey;
            await using (var db = h.NewContext())
            {
                sampleKey = await db.Tasks.Where(t => t.Id == taskId).Select(t => t.Sample!.BlobKey).SingleAsync(Ct);
                db.Add(new Artifact { Id = Guid.NewGuid(), TaskId = taskId, Kind = ArtifactKind.Screenshot, BlobKey = artifactKey, Sha256 = new string('a', 64), Size = 1, CreatedAt = DateTimeOffset.UtcNow });
                db.Add(new HttpTransaction { TaskId = taskId, Method = "GET", Url = "http://x.test/", Host = "x.test", BodyBlobKey = bodyKey });
                await db.SaveChangesAsync(Ct);
            }
            foreach (var k in new[] { artifactKey, bodyKey, sampleKey, orphan }) Age(h, k, TimeSpan.FromHours(5));

            var result = await h.Get<BlobGarbageCollector>().SweepAsync(DateTimeOffset.UtcNow, Ct);

            Assert.Equal(new GcResult(1, 0), result);
            Assert.False(h.Get<IArtifactStore>().Exists(orphan));
            Assert.True(h.Get<IArtifactStore>().Exists(artifactKey));
            Assert.True(h.Get<IArtifactStore>().Exists(bodyKey));
            Assert.True(h.Get<IArtifactStore>().Exists(sampleKey));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task Sweep_RespectsGracePeriod()
    {
        var temp = NewTempDir();
        try
        {
            await using var h = await OrchestratorHarness.CreateAsync(sql, Settings(temp));
            var fresh = await PutAsync(h);
            var old = await PutAsync(h);
            Age(h, fresh, TimeSpan.FromMinutes(10));
            Age(h, old, TimeSpan.FromMinutes(90));

            var result = await h.Get<BlobGarbageCollector>().SweepAsync(DateTimeOffset.UtcNow, Ct);

            Assert.Equal(new GcResult(1, 0), result);
            Assert.True(h.Get<IArtifactStore>().Exists(fresh));
            Assert.False(h.Get<IArtifactStore>().Exists(old));

            // Nothing deleted means no audit row for a second pass.
            await h.Get<BlobGarbageCollector>().SweepAsync(DateTimeOffset.UtcNow, Ct);
            await using var db = h.NewContext();
            Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == "retention.blob_gc", Ct));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task Sweep_DeletesStaleScanTempFiles()
    {
        var temp = NewTempDir();
        try
        {
            await using var h = await OrchestratorHarness.CreateAsync(sql, Settings(temp));
            var staleScan = Path.Combine(temp, "maldex-scan-" + Guid.NewGuid().ToString("N"));
            var staleSuricata = Path.Combine(temp, "maldex-suricata-" + Guid.NewGuid().ToString("N"));
            var freshScan = Path.Combine(temp, "maldex-scan-" + Guid.NewGuid().ToString("N"));
            var unrelated = Path.Combine(temp, "maldex-other-" + Guid.NewGuid().ToString("N"));
            var unrelated2 = Path.Combine(temp, "notmaldex-scan-1");
            File.WriteAllText(staleScan, "x");
            Directory.CreateDirectory(staleSuricata);
            File.WriteAllText(Path.Combine(staleSuricata, "eve.json"), "{}");
            File.WriteAllText(freshScan, "x");
            File.WriteAllText(unrelated, "x");
            File.WriteAllText(unrelated2, "x");
            var old = DateTime.UtcNow.AddHours(-5);
            File.SetLastWriteTimeUtc(staleScan, old);
            Directory.SetLastWriteTimeUtc(staleSuricata, old);
            File.SetLastWriteTimeUtc(unrelated, old);
            File.SetLastWriteTimeUtc(unrelated2, old);

            var result = await h.Get<BlobGarbageCollector>().SweepAsync(DateTimeOffset.UtcNow, Ct);

            Assert.Equal(new GcResult(0, 2), result);
            Assert.False(File.Exists(staleScan));
            Assert.False(Directory.Exists(staleSuricata));
            Assert.True(File.Exists(freshScan));
            Assert.True(File.Exists(unrelated));
            Assert.True(File.Exists(unrelated2));
            await using var db = h.NewContext();
            Assert.Contains("temp_files=2", (await db.AuditLogs.SingleAsync(a => a.Action == "retention.blob_gc", Ct)).Details);
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task Sweep_KeepsOrphanRefreshedByDedupedReupload()
    {
        var temp = NewTempDir();
        try
        {
            await using var h = await OrchestratorHarness.CreateAsync(sql, Settings(temp));
            var bytes = Encoding.ASCII.GetBytes("dedup " + Guid.NewGuid());
            var store = h.Get<IArtifactStore>();
            await using var first = new MemoryStream(bytes);
            var key = (await store.PutAsync("screenshots", first, 1_048_576, Ct)).Key;
            Age(h, key, TimeSpan.FromHours(5));
            await using var second = new MemoryStream(bytes);
            Assert.Equal(key, (await store.PutAsync("screenshots", second, 1_048_576, Ct)).Key);

            var result = await h.Get<BlobGarbageCollector>().SweepAsync(DateTimeOffset.UtcNow, Ct);

            Assert.Equal(new GcResult(0, 0), result);
            Assert.True(store.Exists(key));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task Sweep_RemovesScanSymlinkButNotItsTarget()
    {
        var temp = NewTempDir();
        var outside = NewTempDir();
        try
        {
            await using var h = await OrchestratorHarness.CreateAsync(sql, Settings(temp));
            var outFile = Path.Combine(outside, "keep.txt");
            File.WriteAllText(outFile, "x");
            var fileLink = Path.Combine(temp, "maldex-scan-" + Guid.NewGuid().ToString("N"));
            var dirLink = Path.Combine(temp, "maldex-suricata-" + Guid.NewGuid().ToString("N"));
            File.CreateSymbolicLink(fileLink, outFile);
            Directory.CreateSymbolicLink(dirLink, outside);
            // Age the links and targets.
            var old = DateTime.UtcNow.AddHours(-5);
            File.SetLastWriteTimeUtc(outFile, old);
            Directory.SetLastWriteTimeUtc(outside, old);
            new FileInfo(fileLink).LastWriteTimeUtc = old;
            new FileInfo(dirLink).LastWriteTimeUtc = old;

            var result = await h.Get<BlobGarbageCollector>().SweepAsync(DateTimeOffset.UtcNow, Ct);

            Assert.Equal(2, result.TempFilesDeleted);
            Assert.False(Path.Exists(fileLink));
            Assert.False(Path.Exists(dirLink));
            Assert.True(File.Exists(outFile));
            Assert.True(Directory.Exists(outside));
        }
        finally { Directory.Delete(temp, true); Directory.Delete(outside, true); }
    }
}

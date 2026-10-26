using System.Text;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Integration.Tests.Orchestrator;
using Maldex.Processing;
using Maldex.Data.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using OrgEntity = Maldex.Core.Domain.Org;

namespace Maldex.Integration.Tests.Processing;

[Collection("sql")]
public class RetentionTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Now => new(2030, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Seeded(Guid TaskId, string? SampleSha, string? SampleKey, List<string> ArtifactKeys);

    private static async Task<Guid> SeedOrgAsync(OrchestratorHarness h, int retentionDays)
    {
        await using var db = h.NewContext();
        var org = new OrgEntity { Id = Guid.NewGuid(), Name = "Org-" + Guid.NewGuid().ToString("N"), RetentionDays = retentionDays };
        db.Add(org);
        await db.SaveChangesAsync(Ct);
        return org.Id;
    }

    private static async Task<(string Sha, string Key)> SeedSampleAsync(OrchestratorHarness h)
    {
        await using var content = new MemoryStream(Encoding.ASCII.GetBytes("sample " + Guid.NewGuid()));
        var blob = await h.Get<IArtifactStore>().PutAsync("samples", content, 1_048_576, Ct);
        await using var db = h.NewContext();
        db.Add(new Sample { Sha256 = blob.Sha256, Sha1 = blob.Sha1, Md5 = blob.Md5, Size = blob.Size, BlobKey = blob.Key, FirstSeen = Now.AddDays(-400) });
        await db.SaveChangesAsync(Ct);
        return (blob.Sha256, blob.Key);
    }

    /// <summary>Seeds a task with a process tree, events, a detection, history and two screenshot artifacts (rows + blobs).</summary>
    private static async Task<Seeded> SeedTaskAsync(OrchestratorHarness h, Guid orgId, RunStatus status, DateTimeOffset createdAt, DateTimeOffset? completedAt,
        string? sampleSha = null)
    {
        var store = h.Get<IArtifactStore>();
        var keys = new List<string>();
        var artifacts = new List<Artifact>();
        var taskId = Guid.NewGuid();
        for (var i = 0; i < 2; i++)
        {
            await using var content = new MemoryStream(Encoding.ASCII.GetBytes("shot " + Guid.NewGuid()));
            var blob = await store.PutAsync("screenshots", content, 1_048_576, Ct);
            keys.Add(blob.Key);
            artifacts.Add(new Artifact { Id = Guid.NewGuid(), TaskId = taskId, Kind = ArtifactKind.Screenshot, BlobKey = blob.Key, Sha256 = blob.Sha256, Size = blob.Size, CreatedAt = createdAt });
        }

        await using var db = h.NewContext();
        var user = TestUsers.Create(UserRole.Analyst, "A");
        var task = new AnalysisTask
        {
            Id = taskId, OrgId = orgId, SubmittedByUserId = user.Id, SampleSha256 = sampleSha, Url = sampleSha is null ? "http://example.test/" : null,
            Image = "win10", NetworkMode = NetworkMode.Simulated, DurationSec = 300, CreatedAt = createdAt, Status = status, CompletedAt = completedAt,
        };
        task.StatusHistory.Add(new TaskStatusHistory { TaskId = taskId, From = null, To = RunStatus.Queued, At = createdAt, Reason = "seed" });
        db.AddRange(user, task);
        await db.SaveChangesAsync(Ct);

        var root = new ProcessNode { TaskId = taskId, Pid = 100, ParentPid = 4, ImagePath = "a.exe", CommandLine = "a.exe", StartedAt = createdAt };
        db.Add(root);
        await db.SaveChangesAsync(Ct);
        var child = new ProcessNode { TaskId = taskId, Pid = 200, ParentPid = 100, ParentNodeId = root.Id, ImagePath = "b.exe", CommandLine = "b.exe", StartedAt = createdAt };
        db.Add(child);
        await db.SaveChangesAsync(Ct);
        db.AddRange(
            new BehaviorEvent { TaskId = taskId, ProcessNodeId = child.Id, Pid = 200, Type = EventType.File, At = createdAt, Summary = "write" },
            new Detection { TaskId = taskId, Source = DetectionSource.Heuristic, Rule = "r", Tags = "", Target = "t" });
        db.AddRange(artifacts);
        await db.SaveChangesAsync(Ct);

        var sampleKey = sampleSha is null ? null : await db.Samples.Where(s => s.Sha256 == sampleSha).Select(s => s.BlobKey).SingleAsync(Ct);
        return new Seeded(taskId, sampleSha, sampleKey, keys);
    }

    private static async Task<bool> TaskExistsAsync(OrchestratorHarness h, Guid id)
    {
        await using var db = h.NewContext();
        return await db.Tasks.AnyAsync(t => t.Id == id, Ct);
    }

    private static async Task<int> ChildRowsAsync(OrchestratorHarness h, Guid id)
    {
        await using var db = h.NewContext();
        return await db.ProcessNodes.CountAsync(x => x.TaskId == id, Ct)
            + await db.BehaviorEvents.CountAsync(x => x.TaskId == id, Ct)
            + await db.Detections.CountAsync(x => x.TaskId == id, Ct)
            + await db.TaskStatusHistory.CountAsync(x => x.TaskId == id, Ct)
            + await db.Artifacts.CountAsync(x => x.TaskId == id, Ct);
    }

    [Fact]
    public async Task PurgesOnlyExpiredTerminalTasks()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var org = await SeedOrgAsync(h, 30);
        var store = h.Get<IArtifactStore>();
        var oldCompleted = await SeedTaskAsync(h, org, RunStatus.Completed, Now.AddDays(-50), Now.AddDays(-45));
        var oldFailed = await SeedTaskAsync(h, org, RunStatus.Failed, Now.AddDays(-50), Now.AddDays(-45));
        // Cancelled is aged by CreatedAt (its CompletedAt is recent here on purpose).
        var oldCancelled = await SeedTaskAsync(h, org, RunStatus.Cancelled, Now.AddDays(-50), Now.AddDays(-1));
        var recentCompleted = await SeedTaskAsync(h, org, RunStatus.Completed, Now.AddDays(-50), Now.AddDays(-5));
        var recentCancelled = await SeedTaskAsync(h, org, RunStatus.Cancelled, Now.AddDays(-5), Now.AddDays(-5));

        var result = await h.Get<RetentionService>().PurgeAsync(Now, Ct);

        Assert.Equal(new RetentionResult(TasksDeleted: 3, BlobsDeleted: 6, SamplesDeleted: 0), result);
        foreach (var gone in new[] { oldCompleted, oldFailed, oldCancelled })
        {
            Assert.False(await TaskExistsAsync(h, gone.TaskId));
            Assert.Equal(0, await ChildRowsAsync(h, gone.TaskId));
            Assert.All(gone.ArtifactKeys, k => Assert.False(store.Exists(k)));
        }
        foreach (var kept in new[] { recentCompleted, recentCancelled })
        {
            Assert.True(await TaskExistsAsync(h, kept.TaskId));
            Assert.Equal(7, await ChildRowsAsync(h, kept.TaskId));
            Assert.All(kept.ArtifactKeys, k => Assert.True(store.Exists(k)));
        }

        await using var db = h.NewContext();
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "retention.purge", Ct);
        Assert.Null(audit.UserId);
        Assert.Contains("tasks=3", audit.Details);
        Assert.Contains("blobs=6", audit.Details);
        Assert.Contains("samples=0", audit.Details);
    }

    [Theory]
    [InlineData(RunStatus.Queued)]
    [InlineData(RunStatus.Preparing)]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Collecting)]
    [InlineData(RunStatus.Processing)]
    public async Task NeverPurgesRunningTask_EvenIfOld(RunStatus status)
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var org = await SeedOrgAsync(h, 1);
        var sample = await SeedSampleAsync(h);
        var t = await SeedTaskAsync(h, org, status, Now.AddDays(-400), completedAt: null, sampleSha: sample.Sha);

        var result = await h.Get<RetentionService>().PurgeAsync(Now, Ct);

        Assert.Equal(new RetentionResult(0, 0, 0), result);
        Assert.True(await TaskExistsAsync(h, t.TaskId));
        Assert.All(t.ArtifactKeys, k => Assert.True(h.Get<IArtifactStore>().Exists(k)));
        Assert.True(h.Get<IArtifactStore>().Exists(sample.Key));
    }

    [Fact]
    public async Task SharedSample_KeptWhileReferenced()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var org = await SeedOrgAsync(h, 30);
        var store = h.Get<IArtifactStore>();
        var sample = await SeedSampleAsync(h);
        var expired = await SeedTaskAsync(h, org, RunStatus.Completed, Now.AddDays(-60), Now.AddDays(-59), sample.Sha);
        var surviving = await SeedTaskAsync(h, org, RunStatus.Completed, Now.AddDays(-2), Now.AddDays(-1), sample.Sha);

        var result = await h.Get<RetentionService>().PurgeAsync(Now, Ct);

        Assert.Equal(new RetentionResult(TasksDeleted: 1, BlobsDeleted: 2, SamplesDeleted: 0), result);
        Assert.False(await TaskExistsAsync(h, expired.TaskId));
        Assert.True(await TaskExistsAsync(h, surviving.TaskId));
        await using var db = h.NewContext();
        Assert.True(await db.Samples.AnyAsync(s => s.Sha256 == sample.Sha, Ct));
        Assert.True(store.Exists(sample.Key));

        // Once the last referencing task expires too, the sample goes with it.
        var later = Now.AddDays(60);
        var second = await h.Get<RetentionService>().PurgeAsync(later, Ct);
        Assert.Equal(new RetentionResult(1, 2, 1), second);
        await using var check = h.NewContext();
        Assert.False(await check.Samples.AnyAsync(s => s.Sha256 == sample.Sha, Ct));
        Assert.False(store.Exists(sample.Key));
    }

    [Fact]
    public async Task OrphanSample_DeletedWithBlob()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var org = await SeedOrgAsync(h, 30);
        var store = h.Get<IArtifactStore>();
        var viaTask = await SeedSampleAsync(h);
        var alreadyOrphan = await SeedSampleAsync(h);
        var referenced = await SeedSampleAsync(h);
        await SeedTaskAsync(h, org, RunStatus.Completed, Now.AddDays(-60), Now.AddDays(-59), viaTask.Sha);
        var live = await SeedTaskAsync(h, org, RunStatus.Running, Now.AddDays(-60), null, referenced.Sha);

        var result = await h.Get<RetentionService>().PurgeAsync(Now, Ct);

        Assert.Equal(new RetentionResult(TasksDeleted: 1, BlobsDeleted: 2, SamplesDeleted: 2), result);
        await using var db = h.NewContext();
        Assert.False(await db.Samples.AnyAsync(s => s.Sha256 == viaTask.Sha, Ct));
        Assert.False(store.Exists(viaTask.Key));
        Assert.False(await db.Samples.AnyAsync(s => s.Sha256 == alreadyOrphan.Sha, Ct));
        Assert.False(store.Exists(alreadyOrphan.Key));
        Assert.True(await db.Samples.AnyAsync(s => s.Sha256 == referenced.Sha, Ct));
        Assert.True(store.Exists(referenced.Key));
        Assert.True(await TaskExistsAsync(h, live.TaskId));
    }

    [Fact]
    public async Task PerOrgRetentionRespected()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var shortOrg = await SeedOrgAsync(h, 1);
        var longOrg = await SeedOrgAsync(h, 30);
        var inShort = await SeedTaskAsync(h, shortOrg, RunStatus.Completed, Now.AddDays(-11), Now.AddDays(-10));
        var inLong = await SeedTaskAsync(h, longOrg, RunStatus.Completed, Now.AddDays(-11), Now.AddDays(-10));

        var result = await h.Get<RetentionService>().PurgeAsync(Now, Ct);

        Assert.Equal(new RetentionResult(1, 2, 0), result);
        Assert.False(await TaskExistsAsync(h, inShort.TaskId));
        Assert.True(await TaskExistsAsync(h, inLong.TaskId));
        Assert.All(inLong.ArtifactKeys, k => Assert.True(h.Get<IArtifactStore>().Exists(k)));
    }

    [Fact]
    public async Task ArtifactBlobSharedWithSurvivingTask_IsKept()
    {
        // Blobs are content-addressed, so identical screenshots in two tasks share one file.
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var org = await SeedOrgAsync(h, 30);
        var store = h.Get<IArtifactStore>();
        var expired = await SeedTaskAsync(h, org, RunStatus.Completed, Now.AddDays(-60), Now.AddDays(-59));
        var surviving = await SeedTaskAsync(h, org, RunStatus.Completed, Now.AddDays(-2), Now.AddDays(-1));
        await using (var db = h.NewContext())
        {
            var shared = await db.Artifacts.SingleAsync(a => a.BlobKey == expired.ArtifactKeys[0], Ct);
            db.Add(new Artifact { Id = Guid.NewGuid(), TaskId = surviving.TaskId, Kind = ArtifactKind.Screenshot, BlobKey = shared.BlobKey, Sha256 = shared.Sha256, Size = shared.Size, CreatedAt = Now });
            await db.SaveChangesAsync(Ct);
            var result = await h.Get<RetentionService>().PurgeAsync(Now, Ct);
            Assert.Equal(new RetentionResult(1, 1, 0), result);
            Assert.True(store.Exists(shared.BlobKey));
        }
        Assert.False(store.Exists(expired.ArtifactKeys[1]));
    }

    private sealed class RecordingTemplateStore : ITemplateStore
    {
        public Task CreateBlankAsync(string stem, long diskBytes, string varsTemplatePath, CancellationToken ct) => throw new NotSupportedException();
        public List<string> Deleted { get; } = [];
        public bool Exists(string fileStem) => true;
        public long? SizeOf(string fileStem) => null;
        public Task DeleteAsync(string fileStem, CancellationToken ct) { lock (Deleted) Deleted.Add(fileStem); return Task.CompletedTask; }
    }

    private static Task<OrchestratorHarness> TemplateHarnessAsync(SqlServerFixture sql, RecordingTemplateStore store) =>
        OrchestratorHarness.CreateAsync(sql, new Dictionary<string, string?> { ["Maldex:Templates:KeepPrevious"] = "0" },
            s => s.Replace(ServiceDescriptor.Singleton<ITemplateStore>(store)));

    /// <summary>Adds Ready v2 to "win10" and makes it current directly (v1 stays Ready).</summary>
    private static async Task<TemplateVersion> AddCurrentV2Async(OrchestratorHarness h)
    {
        await using var db = h.NewContext();
        var v2 = h.NewVersion("win10", 2);
        db.TemplateVersions.Add(v2);
        await db.SaveChangesAsync(Ct);
        (await db.Templates.SingleAsync(t => t.Id == "win10", Ct)).CurrentVersionId = v2.Id;
        // v1 was current until v2 took over (what a publish records), so a rollback can return to it.
        (await db.TemplateVersions.SingleAsync(v => v.TemplateId == "win10" && v.Number == 1, Ct)).CurrentUntil = h.Time.GetUtcNow();
        await db.SaveChangesAsync(Ct);
        return v2;
    }

    [Fact]
    public async Task RetentionWorker_SweepsRetiredUnusedVersion()
    {
        var store = new RecordingTemplateStore();
        await using var h = await TemplateHarnessAsync(sql, store);
        var v2 = await AddCurrentV2Async(h);
        await using (var scope = h.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<TemplateCatalog>().PublishAsync("win10", v2.Id, Ct); // KeepPrevious 0: v1 retired
        Guid v1Id, v2Id = v2.Id;
        string v1Stem;
        await using (var db = h.NewContext())
        {
            var v1 = await db.TemplateVersions.SingleAsync(v => v.TemplateId == "win10" && v.Number == 1, Ct);
            Assert.Equal(TemplateVersionStatus.Retired, v1.Status);
            (v1Id, v1Stem) = (v1.Id, v1.FileStem);
        }
        var worker = h.Get<RetentionWorker>();

        await worker.StartAsync(Ct);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                await using var db = h.NewContext();
                if (await db.TemplateVersions.AnyAsync(v => v.Id == v1Id && v.Status == TemplateVersionStatus.Deleted, Ct)) break;
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "Worker did not sweep within 30 s of real time.");
                h.Time.Advance(TimeSpan.FromMinutes(1));
                await Task.Delay(50, Ct);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await using var check = h.NewContext();
        Assert.Equal([v1Stem], store.Deleted);
        Assert.Equal(TemplateVersionStatus.Ready, (await check.TemplateVersions.SingleAsync(v => v.Id == v2Id, Ct)).Status);
        var audit = await check.AuditLogs.SingleAsync(a => a.Action == "template.version.delete", Ct);
        Assert.Equal(v1Id.ToString(), audit.Target);
        Assert.Equal("template=win10; v=1", audit.Details);
        Assert.Null(audit.UserId);
    }

    [Fact]
    public async Task RollBack_DuringRunningTask_DoesNotDeleteItsVersion()
    {
        var store = new RecordingTemplateStore();
        await using var h = await TemplateHarnessAsync(sql, store);
        var v2 = await AddCurrentV2Async(h);
        var taskId = await h.SeedTaskAsync(RunStatus.Running);
        await using (var db = h.NewContext())
        {
            (await db.Tasks.SingleAsync(t => t.Id == taskId, Ct)).TemplateVersionId = v2.Id;
            await db.SaveChangesAsync(Ct);
        }
        await using (var scope = h.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<TemplateCatalog>().RollBackAsync("win10", Ct); // v1 current again; v2 retired
        var worker = h.Get<RetentionWorker>();

        await worker.SweepTemplatesAsync(Ct);

        Assert.Empty(store.Deleted);
        await using (var db = h.NewContext())
        {
            Assert.Equal(TemplateVersionStatus.Retired, (await db.TemplateVersions.SingleAsync(v => v.Id == v2.Id, Ct)).Status);
            var task = await db.Tasks.SingleAsync(t => t.Id == taskId, Ct);
            foreach (var step in new[] { RunStatus.Collecting, RunStatus.Processing, RunStatus.Completed })
                task.TransitionTo(step, "done", h.Time.GetUtcNow());
            await db.SaveChangesAsync(Ct);
        }

        await worker.SweepTemplatesAsync(Ct);

        Assert.Equal([v2.FileStem], store.Deleted);
        await using var check = h.NewContext();
        Assert.Equal(TemplateVersionStatus.Deleted, (await check.TemplateVersions.SingleAsync(v => v.Id == v2.Id, Ct)).Status);
        Assert.Equal(v2.Id.ToString(), (await check.AuditLogs.SingleAsync(a => a.Action == "template.version.delete", Ct)).Target);
    }

    [Fact]
    public async Task Worker_PurgesAfterStartupDelay()
    {
        await using var h = await OrchestratorHarness.CreateAsync(sql);
        var org = await SeedOrgAsync(h, 1);
        // Harness time is 2030-01-01; seed relative to it.
        var t0 = h.Time.GetUtcNow();
        var expired = await SeedTaskAsync(h, org, RunStatus.Completed, t0.AddDays(-10), t0.AddDays(-9));
        var worker = h.Get<RetentionWorker>();

        await worker.StartAsync(Ct);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Nothing happens before the 5-minute startup delay has elapsed.
            h.Time.Advance(TimeSpan.FromMinutes(4));
            await Task.Delay(100, Ct);
            Assert.True(await TaskExistsAsync(h, expired.TaskId));

            while (await TaskExistsAsync(h, expired.TaskId))
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "Worker did not purge within 30 s of real time.");
                h.Time.Advance(TimeSpan.FromMinutes(1));
                await Task.Delay(50, Ct);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}

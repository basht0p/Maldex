using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Infrastructure.Libvirt;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

[Collection("sql")]
public sealed class SystemStatusTests(SqlServerFixture sql) : IDisposable
{
    private const string Seeded = "win10-ltsc-2021"; // imported at startup

    private readonly List<MaldexAppFactory> _factories = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() { foreach (var f in _factories) f.Dispose(); }

    private MaldexAppFactory Factory(bool workers = false)
    {
        var f = new MaldexAppFactory(sql, workers: workers);
        _factories.Add(f);
        return f;
    }

    private static async Task<JsonElement> StatusAsync(HttpClient admin)
    {
        var resp = await admin.GetAsync("/api/admin/status", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<Guid> SubmitUrlAsync(HttpClient c, Guid org, string url)
    {
        using var form = new MultipartFormDataContent { { new StringContent(org.ToString()), "orgId" }, { new StringContent(url), "url" } };
        var resp = await c.PostAsync("/api/tasks", form, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Status_requires_admin()
    {
        var f = Factory();
        var analyst = f.CreateClient(UserRole.Analyst);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.GetAsync("/api/admin/status", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient(UserRole.Admin).GetAsync("/api/admin/status", Ct)).StatusCode);
    }

    [Fact]
    public async Task Status_lists_slot_holders_counts_and_recent_failures()
    {
        var f = Factory();
        var admin = f.CreateClient(UserRole.Admin);
        var org = (await (await admin.PostAsJsonAsync("/api/orgs", new { name = "Acme", retentionDays = 30 }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("id").GetGuid();

        var running = await SubmitUrlAsync(admin, org, "https://running.example/");
        await SubmitUrlAsync(admin, org, "https://queued.example/");
        var failedIds = new List<Guid>();
        for (var i = 0; i < 12; i++) failedIds.Add(await SubmitUrlAsync(admin, org, $"https://failed{i}.example/"));

        var t0 = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        Guid jobId = Guid.NewGuid(), failedJobId = Guid.NewGuid();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            var userId = (await db.Tasks.FirstAsync(Ct)).SubmittedByUserId;
            var r = await db.Tasks.SingleAsync(t => t.Id == running, Ct);
            r.Status = RunStatus.Running;
            r.StartedAt = t0;
            for (var i = 0; i < failedIds.Count; i++)
            {
                var t = await db.Tasks.SingleAsync(x => x.Id == failedIds[i], Ct);
                t.Status = RunStatus.Failed;
                t.FailureReason = $"boom {i}";
                t.EndedAt = t0.AddMinutes(10 + i * 2); // failed0 .. failed11, oldest first
            }
            db.TemplateJobs.Add(new TemplateJob
            {
                Id = jobId, Kind = TemplateJobKind.Build, TemplateId = Seeded, State = TemplateJobState.Configuring,
                RequestedByUserId = userId, CreatedAt = t0, SlotHeldAt = t0.AddMinutes(1),
            });
            db.TemplateJobs.Add(new TemplateJob
            {
                Id = failedJobId, Kind = TemplateJobKind.Build, TemplateId = Seeded, State = TemplateJobState.Failed,
                FailureReason = "disk full", RequestedByUserId = userId, CreatedAt = t0, EndedAt = t0.AddMinutes(40), // newer than every failed task
            });
            await db.SaveChangesAsync(Ct);
        }

        var s = await StatusAsync(admin);
        var slot = s.GetProperty("vmSlot");
        Assert.Equal(1, slot.GetProperty("capacity").GetInt32());
        Assert.Equal(2, slot.GetProperty("busy").GetInt32());
        var holders = slot.GetProperty("holders").EnumerateArray().ToList();
        Assert.Equal(2, holders.Count);
        Assert.Equal("task", holders[0].GetProperty("kind").GetString());
        Assert.Equal("https://running.example/", holders[0].GetProperty("label").GetString());
        Assert.Equal(t0, holders[0].GetProperty("since").GetDateTimeOffset());
        Assert.Equal("job", holders[1].GetProperty("kind").GetString());
        Assert.Equal($"Build {Seeded}", holders[1].GetProperty("label").GetString());
        Assert.Equal(t0.AddMinutes(1), holders[1].GetProperty("since").GetDateTimeOffset());

        Assert.Equal(1, s.GetProperty("queueDepth").GetInt32());
        Assert.Equal(1, s.GetProperty("runningTasks").GetInt32());
        Assert.Equal(1, s.GetProperty("activeJobs").GetInt32());

        var failures = s.GetProperty("recentFailures").EnumerateArray().ToList();
        Assert.Equal(10, failures.Count);
        Assert.Equal("job", failures[0].GetProperty("kind").GetString());
        Assert.Equal("disk full", failures[0].GetProperty("reason").GetString());
        Assert.Equal(failedJobId, failures[0].GetProperty("id").GetGuid());
        // then the nine newest failed tasks, newest first: failed11 .. failed3
        Assert.Equal(Enumerable.Range(3, 9).Reverse().Select(i => failedIds[i]), failures.Skip(1).Select(x => x.GetProperty("id").GetGuid()));
        Assert.All(failures.Skip(1), x => Assert.Equal("task", x.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task Status_reports_all_six_workers()
    {
        var f = Factory(workers: true);
        var s = await StatusAsync(f.CreateClient(UserRole.Admin));
        var workers = s.GetProperty("workers").EnumerateArray().ToList();
        Assert.Equal(["media", "media-mount-sweep", "orchestrator", "processing", "retention", "template-jobs"],
            workers.Select(w => w.GetProperty("name").GetString()));
        var byName = workers.ToDictionary(w => w.GetProperty("name").GetString()!);
        Assert.Equal(1, byName["orchestrator"].GetProperty("expectedIntervalSec").GetInt32());
        Assert.Equal(2, byName["processing"].GetProperty("expectedIntervalSec").GetInt32());
        Assert.Equal(3600, byName["media"].GetProperty("expectedIntervalSec").GetInt32());
        Assert.Equal((int)(TimeSpan.FromMinutes(5) + TimeSpan.FromHours(24)).TotalSeconds, byName["retention"].GetProperty("expectedIntervalSec").GetInt32());
    }

    [Fact]
    public async Task Missing_disk_path_reports_the_nearest_existing_ancestor()
    {
        var f = Factory();
        var missing = Path.Combine(f.ArtifactRoot, "no", "such", "base");
        var host = f.WithWebHostBuilder(b => b.ConfigureServices(s => s.PostConfigure<LibvirtOptions>(o => o.BaseImageDir = missing)));
        Directory.CreateDirectory(f.ArtifactRoot);

        using var _ = host;
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Role", nameof(UserRole.Admin));
        client.DefaultRequestHeaders.Add("X-Dev-User", "tester");
        var s = await StatusAsync(client);
        var disks = s.GetProperty("disks").EnumerateArray().ToDictionary(d => d.GetProperty("name").GetString()!);
        Assert.Equal(["data", "media", "vm"], disks.Keys.Order());
        var vm = disks["vm"];
        Assert.Equal(Path.GetFullPath(f.ArtifactRoot), vm.GetProperty("path").GetString());
        Assert.True(vm.GetProperty("totalBytes").GetInt64() > 0);
        Assert.False(Directory.Exists(missing));
    }
}

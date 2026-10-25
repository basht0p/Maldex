using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

[Collection("sql")]
public sealed class TaskReportTests(SqlServerFixture sql) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
    private readonly List<MaldexAppFactory> _factories = [];

    public void Dispose() { foreach (var f in _factories) f.Dispose(); }

    private MaldexAppFactory Factory()
    {
        var f = new MaldexAppFactory(sql);
        _factories.Add(f);
        return f;
    }

    private static async Task<JsonElement> ReportAsync(HttpClient client, Guid id)
    {
        var resp = await client.GetAsync($"/api/tasks/{id}/report", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<Artifact> AddArtifactAsync(MaldexAppFactory f, Guid taskId, ArtifactKind kind, byte[] bytes, string? guestPath = null, DateTimeOffset? at = null)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        await using var content = new MemoryStream(bytes);
        var blob = await store.PutAsync(kind == ArtifactKind.Screenshot ? "screenshots" : "dropped", content, 1 << 20, Ct);
        var art = new Artifact { Id = Guid.NewGuid(), TaskId = taskId, Kind = kind, BlobKey = blob.Key, Sha256 = blob.Sha256, Size = blob.Size, GuestPath = guestPath, CreatedAt = at ?? T0 };
        db.Artifacts.Add(art);
        await db.SaveChangesAsync(Ct);
        return art;
    }

    [Fact]
    public async Task Report_of_completed_task_has_all_sections_in_order()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f, screenshots: 2);
        var drop = await AddArtifactAsync(f, seeded.TaskId, ArtifactKind.Dropped, [1, 2, 3, (byte)'d'], @"C:\Temp\x.bin", T0.AddSeconds(5));
        long txId;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
            await using var body = new MemoryStream("http body"u8.ToArray());
            var blob = await store.PutAsync("http", body, 1 << 20, Ct);
            var tx = new HttpTransaction { TaskId = seeded.TaskId, Method = "GET", Url = "http://bad.test/x", Host = "bad.test", Status = 200, ReqHeaders = "a: b", RespHeaders = "c: d", BodyBlobKey = blob.Key };
            db.HttpTransactions.Add(tx);
            db.NetworkFlows.Add(new NetworkFlow { TaskId = seeded.TaskId, Proto = "tcp", Src = "10.0.0.2", Dst = "10.0.0.9", DstPort = 443, Bytes = 99, StartedAt = T0.AddSeconds(2) });
            db.Detections.Add(new Detection { TaskId = seeded.TaskId, Source = DetectionSource.Heuristic, Rule = "Low_Rule", Severity = 40, Tags = "", Target = "x" });
            await db.SaveChangesAsync(Ct);
            txId = tx.Id;
        }
        using var client = f.CreateClient(UserRole.Analyst);

        var r = await ReportAsync(client, seeded.TaskId);

        Assert.Equal(seeded.TaskId, r.GetProperty("task").GetProperty("id").GetGuid());
        Assert.Equal(0, r.GetProperty("task").GetProperty("queuePosition").GetInt32());
        Assert.Equal(40, r.GetProperty("detections")[1].GetProperty("severity").GetInt32());
        var det = r.GetProperty("detections")[0];
        Assert.Equal(90, det.GetProperty("severity").GetInt32());
        Assert.Equal(["malicious", "test"], det.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Empty(r.GetProperty("detections")[1].GetProperty("tags").EnumerateArray());
        Assert.Equal(2, r.GetProperty("detections").GetArrayLength());
        var procs = r.GetProperty("processes");
        Assert.Equal(2, procs.GetArrayLength());
        Assert.Equal(0, procs[0].GetProperty("depth").GetInt32());
        Assert.Equal(JsonValueKind.Null, procs[0].GetProperty("parentId").ValueKind);
        Assert.Equal(1, procs[1].GetProperty("depth").GetInt32());
        Assert.Equal(procs[0].GetProperty("id").GetInt64(), procs[1].GetProperty("parentId").GetInt64());
        Assert.Equal(1, r.GetProperty("networkFlows").GetArrayLength());
        Assert.Equal(1, r.GetProperty("dns").GetArrayLength());
        var http = r.GetProperty("http");
        Assert.Equal(1, http.GetArrayLength());
        Assert.Equal(txId, http[0].GetProperty("id").GetInt64());
        Assert.True(http[0].GetProperty("hasBody").GetBoolean());
        Assert.Equal(drop.Id, r.GetProperty("droppedFiles")[0].GetProperty("artifactId").GetGuid());
        Assert.Equal(@"C:\Temp\x.bin", r.GetProperty("droppedFiles")[0].GetProperty("guestPath").GetString());
        Assert.Equal(seeded.ScreenshotIds, r.GetProperty("screenshots").EnumerateArray().Select(e => e.GetProperty("artifactId").GetGuid()).ToList());
        Assert.True(r.GetProperty("statusHistory").GetArrayLength() >= 1);
        Assert.Equal(seeded.SampleSha256, r.GetProperty("task").GetProperty("sampleSha256").GetString());
        Assert.Equal(40, r.GetProperty("sha1").GetString()!.Length);
        Assert.Equal(32, r.GetProperty("md5").GetString()!.Length);
        Assert.Equal(seeded.SampleBytes.Length, r.GetProperty("sampleSize").GetInt64());
        Assert.False(r.GetProperty("truncated").GetProperty("processes").GetBoolean());
    }

    [Fact]
    public async Task Dns_rows_split_query_and_answers()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            db.BehaviorEvents.RemoveRange(db.BehaviorEvents.Where(e => e.TaskId == seeded.TaskId && e.Type == EventType.Dns));
            db.BehaviorEvents.AddRange(
                new BehaviorEvent { TaskId = seeded.TaskId, Pid = 200, Type = EventType.Dns, At = T0.AddSeconds(1), Summary = "bad.example.test → 10.0.0.1, 10.0.0.2" },
                new BehaviorEvent { TaskId = seeded.TaskId, Pid = 201, Type = EventType.Dns, At = T0.AddSeconds(2), Summary = "lonely.test" });
            await db.SaveChangesAsync(Ct);
        }
        using var client = f.CreateClient(UserRole.Analyst);

        var dns = (await ReportAsync(client, seeded.TaskId)).GetProperty("dns");

        Assert.Equal(2, dns.GetArrayLength());
        Assert.Equal("bad.example.test", dns[0].GetProperty("query").GetString());
        Assert.Equal(["10.0.0.1", "10.0.0.2"], dns[0].GetProperty("answers").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(200, dns[0].GetProperty("pid").GetInt32());
        Assert.Equal("lonely.test", dns[1].GetProperty("query").GetString());
        Assert.Empty(dns[1].GetProperty("answers").EnumerateArray());
    }

    [Fact]
    public async Task Lists_over_2000_rows_are_cut_and_flagged()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            for (var i = 0; i < 2001; i++)
                db.NetworkFlows.Add(new NetworkFlow { TaskId = seeded.TaskId, Proto = "tcp", Src = "10.0.0.2", Dst = "10.0.0.9", DstPort = 1000 + i % 100, Bytes = i, StartedAt = T0.AddSeconds(i) });
            await db.SaveChangesAsync(Ct);
        }
        using var client = f.CreateClient(UserRole.Analyst);

        var r = await ReportAsync(client, seeded.TaskId);

        Assert.Equal(2000, r.GetProperty("networkFlows").GetArrayLength());
        var t = r.GetProperty("truncated");
        Assert.True(t.GetProperty("networkFlows").GetBoolean());
        Assert.False(t.GetProperty("processes").GetBoolean());
        Assert.False(t.GetProperty("dns").GetBoolean());
        Assert.False(t.GetProperty("http").GetBoolean());
    }

    [Fact]
    public async Task Same_sample_tasks_are_counted_excluding_self()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        Guid orgId;
        using (var scope = f.Services.CreateScope())
            orgId = await scope.ServiceProvider.GetRequiredService<MaldexDbContext>().Tasks.Where(t => t.Id == seeded.TaskId).Select(t => t.OrgId).SingleAsync(Ct);
        using var client = f.CreateClient(UserRole.Analyst);
        var ids = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            using var form = new MultipartFormDataContent { { new StringContent(orgId.ToString()), "orgId" } };
            form.Add(new ByteArrayContent(seeded.SampleBytes), "file", "again.exe");
            var resp = await client.PostAsync("/api/tasks", form, Ct);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            ids.Add((await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
        }
        Guid newest;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            var a = await db.Tasks.SingleAsync(t => t.Id == ids[0], Ct);
            var b = await db.Tasks.SingleAsync(t => t.Id == ids[1], Ct);
            a.CreatedAt = T0.AddDays(1); a.Verdict = Verdict.Suspicious;
            b.CreatedAt = T0.AddDays(2); b.Verdict = Verdict.NoThreats;
            newest = b.Id;
            await db.SaveChangesAsync(Ct);
        }

        var same = (await ReportAsync(client, seeded.TaskId)).GetProperty("samePrevious");

        Assert.Equal(2, same.GetProperty("count").GetInt32());
        Assert.Equal(newest, same.GetProperty("latest").GetProperty("taskId").GetGuid());
        Assert.Equal("NoThreats", same.GetProperty("latest").GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Url_task_report_has_null_hashes_and_no_same_previous()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        Guid orgId;
        using (var scope = f.Services.CreateScope())
            orgId = await scope.ServiceProvider.GetRequiredService<MaldexDbContext>().Tasks.Where(t => t.Id == seeded.TaskId).Select(t => t.OrgId).SingleAsync(Ct);
        using var client = f.CreateClient(UserRole.Analyst);
        using var form = new MultipartFormDataContent { { new StringContent(orgId.ToString()), "orgId" }, { new StringContent("https://example.com/a"), "url" } };
        var resp = await client.PostAsync("/api/tasks", form, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var urlTask = (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            (await db.Tasks.SingleAsync(t => t.Id == urlTask, Ct)).Status = RunStatus.Completed;
            await db.SaveChangesAsync(Ct);
        }

        var r = await ReportAsync(client, urlTask);

        Assert.Equal("https://example.com/a", r.GetProperty("task").GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("sha1").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("md5").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("sampleSize").ValueKind);
        Assert.Equal(0, r.GetProperty("samePrevious").GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("samePrevious").GetProperty("latest").ValueKind);
    }

    [Fact]
    public async Task Report_is_404_for_unknown_and_409_report_not_ready_while_processing()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f, status: RunStatus.Processing);
        using var client = f.CreateClient(UserRole.Analyst);

        var conflict = await client.GetAsync($"/api/tasks/{seeded.TaskId}/report", Ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("report-not-ready", problem.GetProperty("type").GetString());
        Assert.Equal("The report is only available once the task has completed.", problem.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{Guid.NewGuid()}/report", Ct)).StatusCode);
    }

    [Fact]
    public async Task Hostile_strings_round_trip_as_plain_json_text()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        using var client = f.CreateClient(UserRole.Analyst);

        var r = await ReportAsync(client, seeded.TaskId);

        Assert.Contains(r.GetProperty("processes").EnumerateArray(), p => p.GetProperty("commandLine").GetString() == CompletedTaskSeed.HostileCommandLine);
        Assert.Equal(CompletedTaskSeed.HostileOrgName, r.GetProperty("task").GetProperty("orgName").GetString());
    }

    [Fact]
    public async Task Image_endpoint_serves_screenshots_only_with_nosniff()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f, screenshots: 1);
        var dropped = await AddArtifactAsync(f, seeded.TaskId, ArtifactKind.Dropped, [0x89, 0x50, 0x4E, 0x47, 9, 9], @"C:\x.png");
        var gif = await AddArtifactAsync(f, seeded.TaskId, ArtifactKind.Screenshot, "GIF89a-not-allowed"u8.ToArray());
        var jpeg = await AddArtifactAsync(f, seeded.TaskId, ArtifactKind.Screenshot, [0xFF, 0xD8, 0xFF, 0xE0, 5]);
        using var client = f.CreateClient(UserRole.Analyst);

        var ok = await client.GetAsync($"/api/artifacts/{seeded.ScreenshotIds[0]}/image", Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/png", ok.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", ok.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("inline", ok.Content.Headers.ContentDisposition!.ToString());
        Assert.True(ok.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromDays(1), ok.Headers.CacheControl.MaxAge);

        Assert.Equal("image/jpeg", (await client.GetAsync($"/api/artifacts/{jpeg.Id}/image", Ct)).Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/artifacts/{dropped.Id}/image", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/artifacts/{Guid.NewGuid()}/image", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.GetAsync($"/api/artifacts/{gif.Id}/image", Ct)).StatusCode);

        using var scope = f.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<MaldexDbContext>().AuditLogs.CountAsync(a => a.Action == "artifact.download", Ct));
    }

    private static async Task<long> AddHttpAsync(MaldexAppFactory f, Guid taskId, byte[]? body)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        string? key = null;
        if (body is not null)
        {
            await using var content = new MemoryStream(body);
            key = (await store.PutAsync("http", content, 1 << 20, Ct)).Key;
        }
        var tx = new HttpTransaction { TaskId = taskId, Method = "GET", Url = "http://x.test/", Host = "x.test", Status = 200, ReqHeaders = "", RespHeaders = "", BodyBlobKey = key };
        db.HttpTransactions.Add(tx);
        await db.SaveChangesAsync(Ct);
        return tx.Id;
    }

    [Fact]
    public async Task Http_body_downloads_as_protected_zip_and_is_audited()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        var payload = "<html>evil body</html>"u8.ToArray();
        var txId = await AddHttpAsync(f, seeded.TaskId, payload);
        var noBody = await AddHttpAsync(f, seeded.TaskId, null);
        using var client = f.CreateClient(UserRole.Analyst);

        var resp = await client.GetAsync($"/api/tasks/{seeded.TaskId}/http/{txId}/body", Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/zip", resp.Content.Headers.ContentType!.MediaType);
        using var zf = new ZipFile(new MemoryStream(await resp.Content.ReadAsByteArrayAsync(Ct))) { Password = "infected" };
        Assert.Equal(1, zf.Count);
        Assert.Equal($"http-{txId}", zf[0].Name);
        using var s = zf.GetInputStream(zf[0]);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        Assert.Equal(payload, ms.ToArray());

        using var scope = f.Services.CreateScope();
        var row = Assert.Single(await scope.ServiceProvider.GetRequiredService<MaldexDbContext>().AuditLogs.AsNoTracking().Where(a => a.Action == "artifact.download").ToListAsync(Ct));
        Assert.Contains("kind=HttpBody", row.Details);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{seeded.TaskId}/http/{noBody}/body", Ct)).StatusCode);
    }

    [Fact]
    public async Task Http_body_of_another_tasks_transaction_is_404()
    {
        var f = Factory();
        var a = await CompletedTaskSeed.SeedAsync(f);
        Guid orgId;
        using (var scope = f.Services.CreateScope())
            orgId = await scope.ServiceProvider.GetRequiredService<MaldexDbContext>().Tasks.Where(t => t.Id == a.TaskId).Select(t => t.OrgId).SingleAsync(Ct);
        using var client = f.CreateClient(UserRole.Analyst);
        using var form = new MultipartFormDataContent { { new StringContent(orgId.ToString()), "orgId" }, { new StringContent("https://example.com/b"), "url" } };
        var created = await client.PostAsync("/api/tasks", form, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var b = new { TaskId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid() };
        var txId = await AddHttpAsync(f, a.TaskId, [1, 2, 3]);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{b.TaskId}/http/{txId}/body", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/tasks/{a.TaskId}/http/{txId}/body", Ct)).StatusCode);
    }

    [Fact]
    public async Task Report_requires_analyst_auth()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f, screenshots: 1);
        // A second host on the same database, in Entra mode: dev auth would admit an unauthenticated client.
        var entra = new MaldexAppFactory(sql, authMode: "Entra", connectionString: f.ConnectionString, settings: new Dictionary<string, string?>
        {
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000000",
            ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000001",
        });
        _factories.Add(entra);
        using var anonymous = entra.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/tasks/{seeded.TaskId}/report", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/artifacts/{seeded.ScreenshotIds[0]}/image", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/tasks/{seeded.TaskId}/http/1/body", Ct)).StatusCode);
    }
}

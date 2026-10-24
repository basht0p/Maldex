using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

/// <summary>Seeds a task through the real submit endpoint and then fills in what a finished run would have produced.</summary>
internal static class CompletedTaskSeed
{
    public const string HostileCommandLine = "<script>alert(1)</script>";
    public const string HostileOrgName = "Acme <b>&Co";

    public sealed record Seeded(Guid TaskId, string SampleSha256, byte[] SampleBytes, List<Guid> ScreenshotIds);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Seeded> SeedAsync(MaldexAppFactory f, RunStatus status = RunStatus.Completed, Verdict verdict = Verdict.Malicious,
        int screenshots = 0, int extraEvents = 0)
    {
        using var admin = f.CreateClient(UserRole.Admin, "admin");
        var orgResp = await admin.PostAsJsonAsync("/api/orgs", new { name = HostileOrgName, retentionDays = 30 }, Ct);
        orgResp.EnsureSuccessStatusCode();
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var sampleBytes = Encoding.ASCII.GetBytes("report sample " + Guid.NewGuid());
        using var analyst = f.CreateClient(UserRole.Analyst, "analyst");
        using var form = new MultipartFormDataContent { { new StringContent(orgId.ToString()), "orgId" } };
        var file = new ByteArrayContent(sampleBytes);
        form.Add(file, "file", "invoice<i>.exe");
        var resp = await analyst.PostAsync("/api/tasks", form, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var taskId = created.GetProperty("id").GetGuid();
        var sha = created.GetProperty("sampleSha256").GetString()!;

        var shots = new List<Guid>();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        var t0 = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");

        var task = await db.Tasks.SingleAsync(t => t.Id == taskId, Ct);
        task.Status = status;
        task.Verdict = verdict;
        task.StartedAt = t0;
        task.CompletedAt = t0.AddMinutes(5);
        task.ProcessingWarnings = ["yara failed: <warn>"];

        var explorer = new ProcessNode { TaskId = taskId, Pid = 100, ParentPid = 1, ImagePath = @"C:\Windows\explorer.exe", CommandLine = "explorer.exe", User = "analyst", StartedAt = t0 };
        db.ProcessNodes.Add(explorer);
        await db.SaveChangesAsync(Ct);
        var evil = new ProcessNode
        {
            TaskId = taskId, Pid = 200, ParentPid = 100, ParentNodeId = explorer.Id, ImagePath = @"C:\Users\analyst\evil.exe",
            CommandLine = HostileCommandLine, User = "analyst", StartedAt = t0.AddSeconds(1),
        };
        db.ProcessNodes.Add(evil);
        await db.SaveChangesAsync(Ct);

        db.BehaviorEvents.AddRange(
            new BehaviorEvent { TaskId = taskId, ProcessNodeId = evil.Id, Pid = 200, Type = EventType.Process, At = t0.AddSeconds(1), Summary = "started evil.exe" },
            new BehaviorEvent { TaskId = taskId, ProcessNodeId = evil.Id, Pid = 200, Type = EventType.Network, At = t0.AddSeconds(2), Summary = "tcp 10.0.0.9:443 connect", Data = """{"remote":"10.0.0.9:443"}""" },
            new BehaviorEvent { TaskId = taskId, ProcessNodeId = evil.Id, Pid = 200, Type = EventType.Dns, At = t0.AddSeconds(3), Summary = "dns lookup bad.example.test" },
            new BehaviorEvent { TaskId = taskId, ProcessNodeId = evil.Id, Pid = 200, Type = EventType.File, At = t0.AddSeconds(4), Summary = "write C:\\Temp\\x.txt" });
        for (var i = 0; i < extraEvents; i++)
            db.BehaviorEvents.Add(new BehaviorEvent { TaskId = taskId, Pid = 200, Type = EventType.File, At = t0.AddSeconds(10 + i), Summary = $"bulk event {i}" });

        db.Detections.Add(new Detection
        {
            TaskId = taskId, Source = DetectionSource.Yara, Rule = "Evil_Rule_Name", Severity = 90, Tags = "malicious,test",
            Target = "invoice.exe", Details = "matched <details>",
        });

        for (var i = 0; i < screenshots; i++)
        {
            // Each blob is distinct (content-addressed), a tiny PNG-signature payload.
            var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, (byte)i, (byte)(i >> 8), 7 };
            await using var content = new MemoryStream(bytes);
            var blob = await store.PutAsync("screenshots", content, 1 << 20, Ct);
            var art = new Artifact
            {
                Id = Guid.NewGuid(), TaskId = taskId, Kind = ArtifactKind.Screenshot, BlobKey = blob.Key, Sha256 = blob.Sha256,
                Size = blob.Size, CreatedAt = t0.AddSeconds(i),
            };
            db.Artifacts.Add(art);
            shots.Add(art.Id);
        }
        await db.SaveChangesAsync(Ct);
        return new Seeded(taskId, sha, sampleBytes, shots);
    }
}

[Collection("sql")]
public sealed class ReportTests(SqlServerFixture sql) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<MaldexAppFactory> _factories = [];

    public void Dispose() { foreach (var f in _factories) f.Dispose(); }

    private MaldexAppFactory Factory()
    {
        var f = new MaldexAppFactory(sql);
        _factories.Add(f);
        return f;
    }

    [Fact]
    public async Task Report_NotCompleted_409()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f, status: RunStatus.Processing);
        using var client = f.CreateClient(UserRole.Analyst);

        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/tasks/{seeded.TaskId}/report.html", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/tasks/{seeded.TaskId}/report.pdf", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{Guid.NewGuid()}/report.html", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{Guid.NewGuid()}/report.pdf", Ct)).StatusCode);
    }

    [Fact]
    public async Task Report_ContainsVerdictProcessTreeAndDetections()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f, verdict: Verdict.Malicious, screenshots: 2);
        using var client = f.CreateClient(UserRole.Analyst);

        var resp = await client.GetAsync($"/api/tasks/{seeded.TaskId}/report.html", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/html", resp.Content.Headers.ContentType!.MediaType + "");
        var html = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Contains("Malicious", html);
        Assert.Contains("explorer.exe", html);
        Assert.Contains("evil.exe", html);
        Assert.Contains("Evil_Rule_Name", html);
        Assert.Contains("bad.example.test", html);
        Assert.Contains("10.0.0.9:443", html);
        Assert.Contains(seeded.SampleSha256, html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("yara failed:", html);
    }

    [Theory]
    [InlineData(Verdict.NoThreats, "No threats detected")]
    [InlineData(Verdict.Suspicious, "Suspicious")]
    [InlineData(Verdict.Malicious, "Malicious")]
    [InlineData(Verdict.Unknown, "Unknown")]
    public async Task Report_VerdictLabels(Verdict verdict, string label)
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f, verdict: verdict);
        using var client = f.CreateClient(UserRole.Analyst);

        var html = await client.GetStringAsync($"/api/tasks/{seeded.TaskId}/report.html", Ct);
        Assert.Contains($">{label}<", html);
    }

    [Fact]
    public async Task Report_EncodesGuestStrings()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        using var client = f.CreateClient(UserRole.Analyst);

        var html = await client.GetStringAsync($"/api/tasks/{seeded.TaskId}/report.html", Ct);

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        // The hostile file name, org name, detection details and warning are text as well.
        Assert.DoesNotContain("invoice<i>", html);
        Assert.DoesNotContain("Acme <b>", html);
        Assert.DoesNotContain("<details>", html);
        Assert.DoesNotContain("<warn>", html);
        Assert.Contains("invoice&lt;i&gt;.exe", html);
    }

    [Fact]
    public async Task Report_ShowsTemplateLine()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        await TemplateSeed.BindAsync(f, seeded.TaskId);
        using var client = f.CreateClient(UserRole.Analyst);

        var html = await client.GetStringAsync($"/api/tasks/{seeded.TaskId}/report.html", Ct);

        Assert.Contains(">Template<", html);
        Assert.Contains("win11-golden v3 (Windows 11 Pro 26300, agent 1.0.0, sealed 2026-10-24 by basht0p)", html);
        Assert.DoesNotContain(">Image<", html);
    }

    [Fact]
    public async Task Report_OldTask_ShowsImage()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        using var client = f.CreateClient(UserRole.Analyst);

        var html = await client.GetStringAsync($"/api/tasks/{seeded.TaskId}/report.html", Ct);

        Assert.Contains(">Image<", html);
        Assert.DoesNotContain(">Template<", html);
    }

    [Fact]
    public async Task Report_HasStrictCsp()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        using var client = f.CreateClient(UserRole.Analyst);

        var resp = await client.GetAsync($"/api/tasks/{seeded.TaskId}/report.html", Ct);
        Assert.Equal("default-src 'none'; img-src data:; style-src 'unsafe-inline'", Assert.Single(resp.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("inline", resp.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", Assert.Single(resp.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Report_CapsTimelineAt2000_AndScreenshotsAt12()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f, screenshots: 30, extraEvents: 2100);
        using var scope = f.Services.CreateScope();
        var model = await scope.ServiceProvider.GetRequiredService<Maldex.Web.Reports.ReportModelBuilder>().BuildAsync(seeded.TaskId, Ct);

        Assert.Equal(2000, model.Timeline.Count);
        Assert.Equal(12, model.Screenshots.Count);
        Assert.All(model.Screenshots, s => Assert.StartsWith("data:image/png;base64,", s.DataUri));
        Assert.Single(model.NetworkEvents);
        Assert.Single(model.DnsEvents);
        Assert.Equal(2, model.Processes.Count);
    }

    [Fact]
    public async Task Pdf_StartsWithPdfMagic()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f, screenshots: 1);
        using var client = f.CreateClient(UserRole.Analyst);

        var resp = await client.GetAsync($"/api/tasks/{seeded.TaskId}/report.pdf", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/pdf", resp.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", resp.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal($"maldex-{seeded.TaskId}.pdf", resp.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        var bytes = await resp.Content.ReadAsByteArrayAsync(Ct);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public async Task PdfRenderer_Cancelled_Throws()
    {
        // Cancel from a hook the moment rendering starts, so the outcome does not depend on how fast Chromium renders.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var renderer = new Maldex.Web.Reports.PdfRenderer(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
        {
            OnRenderStarting = cts.Cancel,
        };
        var html = "<html><body>" + string.Concat(Enumerable.Repeat("<p>cancel me cancel me cancel me cancel me</p>", 60_000)) + "</body></html>";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderAsync(html, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "cancellation did not abort the render promptly");
    }
}

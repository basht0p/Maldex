using System.Net;
using System.Net.Http.Headers;
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

[Collection("sql")]
public sealed class ResubmitTests(SqlServerFixture sql) : IDisposable
{
    private readonly List<MaldexAppFactory> _factories = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() { foreach (var f in _factories) f.Dispose(); }

    private MaldexAppFactory Factory()
    {
        var f = new MaldexAppFactory(sql);
        _factories.Add(f);
        return f;
    }

    private static async Task<Guid> CreateOrgAsync(HttpClient admin)
    {
        var resp = await admin.PostAsJsonAsync("/api/orgs", new { name = "Acme", retentionDays = 30 }, Ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<JsonElement> SubmitAsync(HttpClient c, Guid org, bool file)
    {
        var form = new MultipartFormDataContent { { new StringContent(org.ToString()), "orgId" } };
        if (file)
        {
            var part = new ByteArrayContent(Encoding.ASCII.GetBytes("resubmit me"));
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "file", "sample.bin");
        }
        else form.Add(new StringContent("https://example.com/x"), "url");
        var resp = await c.PostAsync("/api/tasks", form, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return await Json(resp);
    }

    [Fact]
    public async Task Resubmit_file_task_reuses_the_sample_with_new_options()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var src = await SubmitAsync(client, org, file: true);
        var srcId = src.GetProperty("id").GetGuid();

        var resp = await client.PostAsJsonAsync($"/api/tasks/{srcId}/resubmit", new { durationSec = 120, networkMode = "None" }, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await Json(resp);
        var newId = dto.GetProperty("id").GetGuid();
        Assert.NotEqual(srcId, newId);
        Assert.Equal($"/api/tasks/{newId}", resp.Headers.Location?.OriginalString);
        Assert.Equal(src.GetProperty("sampleSha256").GetString(), dto.GetProperty("sampleSha256").GetString());
        Assert.Equal("sample.bin", dto.GetProperty("originalFileName").GetString());
        Assert.Equal(120, dto.GetProperty("durationSec").GetInt32());
        Assert.Equal("None", dto.GetProperty("networkMode").GetString());
        Assert.Equal("Queued", dto.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Resubmit_url_task_reuses_the_url()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var src = await SubmitAsync(client, org, file: false);

        var resp = await client.PostAsJsonAsync($"/api/tasks/{src.GetProperty("id").GetGuid()}/resubmit", new { durationSec = 90 }, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await Json(resp);
        Assert.Equal(src.GetProperty("url").GetString(), dto.GetProperty("url").GetString());
        Assert.Equal(90, dto.GetProperty("durationSec").GetInt32());
    }

    [Fact]
    public async Task Resubmit_unknown_task_is_404()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var resp = await client.PostAsJsonAsync($"/api/tasks/{Guid.NewGuid()}/resubmit", new { }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Resubmit_after_retention_purge_is_409_sample_unavailable()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var src = await SubmitAsync(client, org, file: true);

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            var sample = await db.Samples.SingleAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<IArtifactStore>().DeleteAsync(sample.BlobKey, Ct);
        }

        var resp = await client.PostAsJsonAsync($"/api/tasks/{src.GetProperty("id").GetGuid()}/resubmit", new { }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await Json(resp);
        Assert.Equal("sample-unavailable", body.GetProperty("type").GetString());
        Assert.Equal("The sample has been deleted under the retention policy.", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Resubmit_validates_options_like_submit()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var src = await SubmitAsync(client, org, file: false);

        var resp = await client.PostAsJsonAsync($"/api/tasks/{src.GetProperty("id").GetGuid()}/resubmit", new { durationSec = 10 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.True((await Json(resp)).GetProperty("errors").TryGetProperty("durationSec", out _));
    }

    [Fact]
    public async Task Resubmit_is_audited_with_source()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var src = await SubmitAsync(client, org, file: false);
        var srcId = src.GetProperty("id").GetGuid();

        var resp = await client.PostAsJsonAsync($"/api/tasks/{srcId}/resubmit", new { }, Ct);
        var newId = (await Json(resp)).GetProperty("id").GetGuid();

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var row = await db.AuditLogs.SingleAsync(a => a.Action == "task.submit" && a.Target == newId.ToString(), Ct);
        Assert.Contains($"resubmittedFrom={srcId}", row.Details);
    }
}

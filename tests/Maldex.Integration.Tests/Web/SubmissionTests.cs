using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

[Collection("sql")]
public sealed class SubmissionTests(SqlServerFixture sql) : IDisposable
{
    private readonly List<MaldexAppFactory> _factories = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() { foreach (var f in _factories) f.Dispose(); }

    private MaldexAppFactory Factory(params (string Key, string Value)[] settings)
    {
        var f = new MaldexAppFactory(sql, settings: settings.ToDictionary(s => s.Key, s => (string?)s.Value));
        _factories.Add(f);
        return f;
    }

    private static async Task<Guid> CreateOrgAsync(HttpClient admin, string name = "Acme")
    {
        var resp = await admin.PostAsJsonAsync("/api/orgs", new { name, retentionDays = 30 }, Ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static MultipartFormDataContent Form(Guid? orgId, byte[]? file = null, string? fileName = "a.bin",
        Dictionary<string, string>? fields = null, bool fileFirst = false)
    {
        var form = new MultipartFormDataContent();
        void AddFile()
        {
            if (file is null) return;
            var part = new ByteArrayContent(file);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = "file",
                FileName = "fallback.bin",
                FileNameStar = fileName,
            };
            form.Add(part);
        }
        if (fileFirst) AddFile();
        if (orgId is not null) form.Add(new StringContent(orgId.Value.ToString()), "orgId");
        if (fields is not null) foreach (var (k, v) in fields) form.Add(new StringContent(v), k);
        if (!fileFirst) AddFile();
        return form;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task AssertBadRequest(HttpResponseMessage r, string key)
    {
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await Json(r);
        Assert.True(body.GetProperty("errors").TryGetProperty(key, out _), $"expected error key '{key}' in {body}");
    }

    [Fact]
    public async Task SubmitFile_CreatesQueuedTask_AndSample()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var bytes = Encoding.ASCII.GetBytes("hello malware");

        var resp = await client.PostAsync("/api/tasks", Form(org, bytes, fileFirst: true), Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await Json(resp);
        Assert.Equal("Queued", dto.GetProperty("status").GetString());
        Assert.Equal("Simulated", dto.GetProperty("networkMode").GetString());
        Assert.Equal("win10-ltsc-2021", dto.GetProperty("image").GetString());
        Assert.Equal(300, dto.GetProperty("durationSec").GetInt32());
        Assert.True(dto.GetProperty("interactive").GetBoolean());
        Assert.Equal("Acme", dto.GetProperty("orgName").GetString());
        Assert.Equal(1, dto.GetProperty("queuePosition").GetInt32());
        Assert.Equal("a.bin", dto.GetProperty("originalFileName").GetString());
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(sha, dto.GetProperty("sampleSha256").GetString());
        var id = dto.GetProperty("id").GetGuid();
        Assert.Equal($"/api/tasks/{id}", resp.Headers.Location?.OriginalString);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var sample = await db.Samples.SingleAsync(Ct);
        Assert.Equal(sha, sample.Sha256);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(bytes)), sample.Sha1);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(bytes)), sample.Md5);
        Assert.Equal(bytes.Length, sample.Size);
        var task = await db.Tasks.Include(t => t.StatusHistory).SingleAsync(Ct);
        var h = Assert.Single(task.StatusHistory);
        Assert.Null(h.From);
        Assert.Equal(RunStatus.Queued, h.To);
        Assert.Equal("submitted", h.Reason);
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == "task.submit" && a.Target == id.ToString(), Ct));

        var got = await Json(await client.GetAsync($"/api/tasks/{id}", Ct));
        Assert.Equal(id, got.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task SubmitUrl_CreatesTask()
    {
        using var f = Factory(("Maldex:Egress:VpnCountries:0", "NL"), ("Maldex:Egress:VpnCountries:1", "DE"));
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));

        var resp = await client.PostAsync("/api/tasks", Form(org, fields: new() { ["url"] = "  https://example.com/x?y=1 " }), Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await Json(resp);
        Assert.Equal("https://example.com/x?y=1", dto.GetProperty("url").GetString());
        Assert.Equal("LiveVpn", dto.GetProperty("networkMode").GetString());
        Assert.Equal("NL", dto.GetProperty("exitCountry").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("sampleSha256").ValueKind);

        // Without any configured country a URL defaults to Simulated.
        using var f2 = Factory();
        using var c2 = f2.CreateClient(UserRole.Analyst, "analyst");
        var org2 = await CreateOrgAsync(f2.CreateClient(UserRole.Admin, "admin"));
        var dto2 = await Json(await c2.PostAsync("/api/tasks", Form(org2, fields: new() { ["url"] = "http://example.com" }), Ct));
        Assert.Equal("Simulated", dto2.GetProperty("networkMode").GetString());
    }

    [Fact]
    public async Task MissingOrg_400()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        await AssertBadRequest(await client.PostAsync("/api/tasks", Form(null, fields: new() { ["url"] = "https://example.com" }), Ct), "orgId");
    }

    [Fact]
    public async Task UnknownOrg_400()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        await AssertBadRequest(await client.PostAsync("/api/tasks", Form(Guid.NewGuid(), fields: new() { ["url"] = "https://example.com" }), Ct), "orgId");
    }

    [Fact]
    public async Task BothFileAndUrl_400()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var resp = await client.PostAsync("/api/tasks", Form(org, [1, 2, 3], fields: new() { ["url"] = "https://example.com" }), Ct);
        await AssertBadRequest(resp, "file");
    }

    [Fact]
    public async Task NeitherFileNorUrl_400()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        await AssertBadRequest(await client.PostAsync("/api/tasks", Form(org), Ct), "file");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/x")]
    [InlineData("not a url")]
    public async Task JavascriptUrl_400(string url)
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        await AssertBadRequest(await client.PostAsync("/api/tasks", Form(org, fields: new() { ["url"] = url }), Ct), "url");
    }

    [Theory]
    [InlineData("https://exa\tmple.com/")]
    [InlineData("https://example.com/a\nb")]
    [InlineData("https://example.com/a b")]
    [InlineData("https://example.com/\u202Etxt")]
    [InlineData("https://@example.com/")]
    public async Task MalformedUrl_400(string url)
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        await AssertBadRequest(await client.PostAsync("/api/tasks", Form(org, fields: new() { ["url"] = url }), Ct), "url");
    }

    [Fact]
    public async Task Url_StoredCanonical()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var dto = await Json(await client.PostAsync("/api/tasks", Form(org, fields: new() { ["url"] = "HTTPS://Example.COM" }), Ct));
        Assert.Equal("https://example.com/", dto.GetProperty("url").GetString());
    }

    [Fact]
    public async Task SecondFileSection_400()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var form = Form(org, [1, 2, 3]);
        form.Add(new ByteArrayContent([4, 5, 6]), "file", "second.bin");
        await AssertBadRequest(await client.PostAsync("/api/tasks", form, Ct), "file");
        Assert.Equal(0, await CountTasks(f));
    }

    [Fact]
    public async Task Cancel_AfterStateChange_409()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var id = (await Json(await client.PostAsync("/api/tasks", Form(org, [1]), Ct))).GetProperty("id").GetGuid();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            var t = await db.Tasks.SingleAsync(x => x.Id == id, Ct);
            t.TransitionTo(RunStatus.Preparing, "picked up", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/tasks/{id}/cancel", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task UrlWithCredentials_400()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        await AssertBadRequest(await client.PostAsync("/api/tasks", Form(org, fields: new() { ["url"] = "https://user:pass@example.com/" }), Ct), "url");
    }

    [Theory]
    [InlineData("59")]
    [InlineData("1801")]
    [InlineData("abc")]
    public async Task DurationOutOfRange_400(string value)
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        await AssertBadRequest(await client.PostAsync("/api/tasks", Form(org, [1], fields: new() { ["durationSec"] = value }), Ct), "durationSec");
    }

    [Fact]
    public async Task LiveTor_400()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var resp = await client.PostAsync("/api/tasks", Form(org, [1], fields: new() { ["networkMode"] = "livetor" }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync(Ct);
        Assert.Contains("networkMode", text);
        Assert.Contains("LiveTor is not available yet", text);
        await AssertBadRequest(await client.PostAsync("/api/tasks", Form(org, [1], fields: new() { ["networkMode"] = "3" }), Ct), "networkMode");
    }

    [Fact]
    public async Task LiveVpnWithoutConfiguredCountry_400()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var resp = await client.PostAsync("/api/tasks", Form(org, [1], fields: new() { ["networkMode"] = "LiveVpn", ["exitCountry"] = "DE" }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, await CountTasks(f));

        using var f2 = Factory(("Maldex:Egress:VpnCountries:0", "NL"));
        using var c2 = f2.CreateClient(UserRole.Analyst, "analyst");
        var org2 = await CreateOrgAsync(f2.CreateClient(UserRole.Admin, "admin"));
        await AssertBadRequest(await c2.PostAsync("/api/tasks", Form(org2, [1], fields: new() { ["networkMode"] = "LiveVpn", ["exitCountry"] = "DE" }), Ct), "exitCountry");
        await AssertBadRequest(await c2.PostAsync("/api/tasks", Form(org2, [1], fields: new() { ["networkMode"] = "Simulated", ["exitCountry"] = "NL" }), Ct), "exitCountry");
    }

    private static async Task AddTemplateAsync(MaldexAppFactory f, string id, string displayName, bool enabled)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        db.Templates.Add(new Template { Id = id, DisplayName = displayName, Enabled = enabled, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task DisableAllTemplatesAsync(MaldexAppFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        await db.Templates.ExecuteUpdateAsync(s => s.SetProperty(t => t.Enabled, false), Ct);
    }

    [Fact]
    public async Task Submit_NoImage_UsesDefaultTemplate()
    {
        using var f = Factory();
        await AddTemplateAsync(f, "aaa-other", "AAA Other", true);
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var resp = await client.PostAsync("/api/tasks", Form(org, [1]), Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal("win10-ltsc-2021", (await Json(resp)).GetProperty("image").GetString());
    }

    [Fact]
    public async Task Submit_ImageDifferentCase_StoresCanonicalId()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var resp = await client.PostAsync("/api/tasks", Form(org, [1], fields: new() { ["image"] = "WIN10-LTSC-2021" }), Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal("win10-ltsc-2021", (await Json(resp)).GetProperty("image").GetString());
    }

    [Fact]
    public async Task Submit_DisabledTemplate_IsRejected()
    {
        using var f = Factory();
        await AddTemplateAsync(f, "off-template", "Off", false);
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var resp = await client.PostAsync("/api/tasks", Form(org, [1], fields: new() { ["image"] = "off-template" }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("Unknown or disabled template.", (await Json(resp)).GetProperty("errors").GetProperty("image")[0].GetString());
        Assert.Equal(0, await CountTasks(f));
    }

    [Fact]
    public async Task Submit_UnknownTemplate_IsRejected()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var resp = await client.PostAsync("/api/tasks", Form(org, [1], fields: new() { ["image"] = "nope" }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("Unknown or disabled template.", (await Json(resp)).GetProperty("errors").GetProperty("image")[0].GetString());
    }

    [Fact]
    public async Task Submit_NoTemplates_Returns409()
    {
        using var f = Factory();
        await DisableAllTemplatesAsync(f);
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        foreach (var fields in new Dictionary<string, string>?[] { null, new() { ["image"] = "win10-ltsc-2021" } })
        {
            var resp = await client.PostAsync("/api/tasks", Form(org, [1], fields: fields), Ct);
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
            Assert.Equal("No sandbox template is available; ask an admin.", (await Json(resp)).GetProperty("title").GetString());
        }
        Assert.Equal(0, await CountTasks(f));
    }

    [Fact]
    public async Task UnknownImage_400()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        await AssertBadRequest(await client.PostAsync("/api/tasks", Form(org, [1], fields: new() { ["image"] = "nope" }), Ct), "image");
    }

    [Fact]
    public async Task UploadOverLimit_413()
    {
        using var f = Factory(("Maldex:MaxUploadBytes", "1024"));
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var resp = await client.PostAsync("/api/tasks", Form(org, new byte[2048]), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        Assert.Contains("1024 bytes", await resp.Content.ReadAsStringAsync(Ct)); // the analyst is told the limit
        Assert.Equal(0, await CountTasks(f));
    }

    /// <summary>A real guest can only receive AgentLimits.MaxInboundBytes: refuse at submit, never after a VM boot.</summary>
    [Fact]
    public void RealMode_UploadCap_IsTheAgentInboundLimit()
    {
        var real = new Maldex.Web.SubmissionOptions { Mode = "Real", MaxUploadBytes = 268_435_456 };
        real.ApplyDefaults();
        Assert.Equal(Maldex.Core.Agent.AgentLimits.MaxInboundBytes, real.MaxUploadBytes);
        var smaller = new Maldex.Web.SubmissionOptions { Mode = "Real", MaxUploadBytes = 1024 };
        smaller.ApplyDefaults();
        Assert.Equal(1024, smaller.MaxUploadBytes);
        var fake = new Maldex.Web.SubmissionOptions { Mode = "Fake", MaxUploadBytes = 268_435_456 };
        fake.ApplyDefaults();
        Assert.Equal(268_435_456, fake.MaxUploadBytes);

        // and the shipped Real settings say so explicitly
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Maldex.slnx"))) dir = Path.GetDirectoryName(dir)!;
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddJsonFile(Path.Combine(dir, "src", "Maldex.Web", "appsettings.Real.json")).Build();
        Assert.Equal(Maldex.Core.Agent.AgentLimits.MaxInboundBytes, long.Parse(cfg["Maldex:MaxUploadBytes"]!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task QueuePosition_InteractiveFirst()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var auto = await Json(await client.PostAsync("/api/tasks", Form(org, [1], fields: new() { ["interactive"] = "false" }), Ct));
        var inter = await Json(await client.PostAsync("/api/tasks", Form(org, [2]), Ct));
        Assert.Equal(2, (await Json(await client.GetAsync($"/api/tasks/{auto.GetProperty("id").GetGuid()}", Ct))).GetProperty("queuePosition").GetInt32());
        Assert.Equal(1, (await Json(await client.GetAsync($"/api/tasks/{inter.GetProperty("id").GetGuid()}", Ct))).GetProperty("queuePosition").GetInt32());

        var list = await Json(await client.GetAsync($"/api/tasks?orgId={org}&status=Queued&take=1000", Ct));
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var items = list.GetProperty("items");
        Assert.Equal(inter.GetProperty("id").GetGuid(), items[0].GetProperty("id").GetGuid()); // newest first
        Assert.Equal(1, items[0].GetProperty("queuePosition").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/tasks?status=bogus", Ct)).StatusCode);
        Assert.Equal(1, (await Json(await client.GetAsync("/api/tasks?take=1", Ct))).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Cancel_Queued_204_ThenStatusCancelled()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var id = (await Json(await client.PostAsync("/api/tasks", Form(org, [1]), Ct))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/tasks/{id}/cancel", null, Ct)).StatusCode);
        var got = await Json(await client.GetAsync($"/api/tasks/{id}", Ct));
        Assert.Equal("Cancelled", got.GetProperty("status").GetString());
        Assert.Equal(0, got.GetProperty("queuePosition").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, got.GetProperty("completedAt").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/tasks/{id}/cancel", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/tasks/{Guid.NewGuid()}/cancel", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{Guid.NewGuid()}", Ct)).StatusCode);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == "task.cancel", Ct));
    }

    [Fact]
    public async Task ConcurrentDuplicateUploads_CreateOneSample()
    {
        using var f = Factory();
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var bytes = RandomNumberGenerator.GetBytes(64 * 1024);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            using var client = f.CreateClient(UserRole.Analyst, "user" + i);
            return await client.PostAsync("/api/tasks", Form(org, bytes), Ct);
        }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        Assert.Equal(10, await db.Tasks.CountAsync(Ct));
        Assert.Equal(1, await db.Samples.CountAsync(Ct));
        var blobs = Directory.GetFiles(Path.Combine(f.ArtifactRoot, "samples"), "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".tmp", StringComparison.Ordinal)).ToList();
        Assert.Single(blobs);
    }

    [Fact]
    public async Task HostileFileName_StoredAsDataOnly()
    {
        using var f = Factory();
        using var client = f.CreateClient(UserRole.Analyst, "analyst");
        var org = await CreateOrgAsync(f.CreateClient(UserRole.Admin, "admin"));
        var bytes = Encoding.ASCII.GetBytes("hostile name sample");
        var name = "..\\..\\evil‮txt.exe" + new string('a', 1000);

        var resp = await client.PostAsync("/api/tasks", Form(org, bytes, name), Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await Json(resp);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(260, dto.GetProperty("originalFileName").GetString()!.Length);
        Assert.StartsWith("..\\..\\evil‮txt.exe", dto.GetProperty("originalFileName").GetString());

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var sample = await db.Samples.SingleAsync(Ct);
        Assert.Equal($"samples/{sha[..2]}/{sha[2..4]}/{sha}", sample.BlobKey);
        Assert.Equal(260, sample.OriginalName!.Length);
        Assert.True(f.Services.GetRequiredService<Maldex.Core.Abstractions.IArtifactStore>().Exists(sample.BlobKey));
        Assert.All(Directory.GetFiles(f.ArtifactRoot, "*", SearchOption.AllDirectories),
            p => Assert.DoesNotContain("evil", p, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<int> CountTasks(MaldexAppFactory f)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MaldexDbContext>().Tasks.CountAsync(Ct);
    }
}

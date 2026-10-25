using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Core.Media;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

[Collection("sql")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class MediaEndpointsTests(SqlServerFixture sql) : IDisposable
{
    private const long MaxChunk = 1024;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IDisposable> _disposables = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maldex-media-web-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        if (Directory.Exists(_root))
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(_root, true);
        }
    }

    private MaldexAppFactory Factory(string authMode = "Dev")
    {
        var f = new MaldexAppFactory(sql, authMode: authMode, settings: new Dictionary<string, string?>
        {
            ["Maldex:Media:Root"] = _root,
            ["Maldex:Media:MaxChunkBytes"] = MaxChunk.ToString(),
            ["Maldex:Media:ChunkBytes"] = "512",
            // Only read in Entra mode: placeholders so the JWT bearer scheme can be built (no token is validated here).
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000000",
            ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000001",
        });
        _disposables.Add(f);
        return f;
    }

    private static async Task<Guid> SeedMediaAsync(MaldexAppFactory f, string name, MediaStatus status = MediaStatus.Ready, DateTimeOffset? addedAt = null,
        MediaContents? contents = null)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var user = new AppUser { Id = Guid.NewGuid(), EntraObjectId = Guid.NewGuid().ToString("N"), DisplayName = "Seeder", Role = UserRole.Admin };
        db.Users.Add(user);
        var now = addedAt ?? DateTimeOffset.UtcNow;
        var media = new MediaFile
        {
            Id = Guid.NewGuid(), Kind = MediaKind.OsInstaller, FileName = name, Size = 100, ReceivedBytes = 100, Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Source = MediaSource.Upload, Status = status, AddedByUserId = user.Id, AddedAt = now, LastActivityAt = now,
            Contents = contents is null ? null : MediaContentsJson.Serialize(contents),
        };
        db.MediaFiles.Add(media);
        await db.SaveChangesAsync(Ct);
        return media.Id;
    }

    private static async Task<JsonElement> StartUploadAsync(HttpClient admin, long size = 100)
    {
        var res = await admin.PostAsJsonAsync("/api/admin/media/uploads", new { fileName = "w.iso", size, kind = "OsInstaller" }, Ct);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static HttpRequestMessage Put(Guid id, byte[] body, string? offset, bool chunked = false)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/media/uploads/{id}");
        if (offset is not null) req.Headers.Add("Upload-Offset", offset);
        if (chunked)
        {
            req.Content = new StreamContent(new MemoryStream(body));
            req.Headers.TransferEncodingChunked = true;
        }
        else
        {
            req.Content = new ByteArrayContent(body);
        }
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return req;
    }

    [Fact]
    public async Task Upload_StartPutAndGet_HappyPath()
    {
        var f = Factory();
        using var admin = f.CreateClient(UserRole.Admin);

        var started = await StartUploadAsync(admin);
        var id = started.GetProperty("media").GetProperty("id").GetGuid();
        Assert.Equal(512, started.GetProperty("chunkBytes").GetInt64());
        Assert.Equal("Receiving", started.GetProperty("media").GetProperty("status").GetString());
        Assert.Equal("OsInstaller", started.GetProperty("media").GetProperty("kind").GetString());

        var put = await admin.SendAsync(Put(id, new byte[40], "0"), Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(40, body.GetProperty("receivedBytes").GetInt64());
        Assert.Equal("Receiving", body.GetProperty("status").GetString());

        var got = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/media/{id}", Ct);
        Assert.Equal(40, got.GetProperty("receivedBytes").GetInt64());
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/admin/media", Ct);
        Assert.Contains(list.EnumerateArray(), m => m.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task List_NewestFirst_AndHidesDeleted()
    {
        var f = Factory();
        using var admin = f.CreateClient(UserRole.Admin);
        var old = await SeedMediaAsync(f, "old.iso", addedAt: DateTimeOffset.UtcNow.AddHours(-2));
        var recent = await SeedMediaAsync(f, "recent.iso", addedAt: DateTimeOffset.UtcNow.AddHours(-1));
        var gone = await SeedMediaAsync(f, "gone.iso", MediaStatus.Deleted);

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/admin/media", Ct);

        Assert.Equal([recent, old], list.EnumerateArray().Select(m => m.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/media/{gone}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Put_WithoutContentLength_411()
    {
        var f = Factory();
        using var admin = f.CreateClient(UserRole.Admin);
        var id = (await StartUploadAsync(admin)).GetProperty("media").GetProperty("id").GetGuid();

        var res = await admin.SendAsync(Put(id, new byte[10], "0", chunked: true), Ct);

        Assert.Equal(HttpStatusCode.LengthRequired, res.StatusCode);
    }

    [Fact]
    public async Task Put_OverMaxChunk_413()
    {
        var f = Factory();
        using var admin = f.CreateClient(UserRole.Admin);
        var id = (await StartUploadAsync(admin, size: 10_000)).GetProperty("media").GetProperty("id").GetGuid();

        var res = await admin.SendAsync(Put(id, new byte[MaxChunk + 1], "0"), Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [Fact]
    public async Task Put_WrongOffset_409_CarriesTitleAndReceivedBytes()
    {
        var f = Factory();
        using var admin = f.CreateClient(UserRole.Admin);
        var id = (await StartUploadAsync(admin)).GetProperty("media").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(Put(id, new byte[30], "0"), Ct)).StatusCode);

        var res = await admin.SendAsync(Put(id, new byte[10], "5"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("the chunk must start at byte 30", problem.GetProperty("title").GetString());
        Assert.Equal(30, problem.GetProperty("receivedBytes").GetInt64());
    }

    [Fact]
    public async Task Put_UnknownUpload_404_AndBadOffset_400()
    {
        var f = Factory();
        using var admin = f.CreateClient(UserRole.Admin);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(Put(Guid.NewGuid(), new byte[10], "0"), Ct)).StatusCode);
        var id = (await StartUploadAsync(admin)).GetProperty("media").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(Put(id, new byte[10], "abc"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(Put(id, new byte[10], null), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("Bogus")]
    public async Task StartUpload_BadKind_400(string kind)
    {
        var f = Factory();
        using var admin = f.CreateClient(UserRole.Admin);

        var res = await admin.PostAsJsonAsync("/api/admin/media/uploads", new { fileName = "w.iso", size = 100, kind }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("kind must be OsInstaller or Application", (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task StartUpload_KindIsCaseInsensitive_AndRuleBreaksAre400()
    {
        var f = Factory();
        using var admin = f.CreateClient(UserRole.Admin);

        var ok = await admin.PostAsJsonAsync("/api/admin/media/uploads", new { fileName = "a.iso", size = 100, kind = "application" }, Ct);
        var empty = await admin.PostAsJsonAsync("/api/admin/media/uploads", new { fileName = "a.iso", size = 0, kind = "Application" }, Ct);

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal("Application", (await ok.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("media").GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("the file is empty", (await empty.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Fetch_Accepted202_AndBadUrlAndKind_400()
    {
        var f = Factory();
        using var admin = f.CreateClient(UserRole.Admin);

        var ok = await admin.PostAsJsonAsync("/api/admin/media/fetch", new { url = "https://example.test/win.iso", kind = "OsInstaller", fileName = "win.iso" }, Ct);
        var badUrl = await admin.PostAsJsonAsync("/api/admin/media/fetch", new { url = "ftp://example.test/win.iso", kind = "OsInstaller" }, Ct);
        var notUrl = await admin.PostAsJsonAsync("/api/admin/media/fetch", new { url = "nope", kind = "OsInstaller" }, Ct);
        var badKind = await admin.PostAsJsonAsync("/api/admin/media/fetch", new { url = "https://example.test/win.iso", kind = "0" }, Ct);

        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        var media = await ok.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Url", media.GetProperty("source").GetString());
        Assert.Equal("win.iso", media.GetProperty("fileName").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notUrl.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
    }

    [Fact]
    public async Task Images_CarrySupported_AndTheHomeReason()
    {
        var f = Factory();
        using var admin = f.CreateClient(UserRole.Admin);
        var id = await SeedMediaAsync(f, "w.iso", contents: new MediaContents(
        [
            new InstallerImage(1, "Windows 11 Home", "Core", "26300.1", "x86_64"),
            new InstallerImage(2, "Windows 11 Pro", "Professional", "26300.1", "x86_64"),
        ], null));

        var m = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/media/{id}", Ct);

        var images = m.GetProperty("images").EnumerateArray().ToList();
        Assert.False(images[0].GetProperty("supported").GetBoolean());
        Assert.Contains("Home is not supported", images[0].GetProperty("unsupportedReason").GetString());
        Assert.True(images[1].GetProperty("supported").GetBoolean());
        Assert.Equal(JsonValueKind.Null, images[1].GetProperty("unsupportedReason").ValueKind);
        Assert.Equal(2, m.GetProperty("preferredImageIndex").GetInt32());
        Assert.Equal("Seeder", m.GetProperty("addedBy").GetString());
    }

    [Fact]
    public async Task UsedBy_ListsAVersionAndAnActiveJob_AndDeleteIsRefused()
    {
        var f = Factory();
        using var admin = f.CreateClient(UserRole.Admin);
        var id = await SeedMediaAsync(f, "w.iso");
        var jobId = Guid.NewGuid();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            var now = DateTimeOffset.UtcNow;
            var userId = db.Users.First().Id;
            db.Templates.Add(new Template { Id = "built", DisplayName = "Built", CreatedAt = now });
            await db.SaveChangesAsync(Ct);
            db.TemplateVersions.Add(new TemplateVersion
            {
                Id = Guid.NewGuid(), TemplateId = "built", Number = 1, Origin = TemplateVersionOrigin.Imported, FileStem = "built-1", SealedAt = now,
                SourceMediaIds = JsonSerializer.Serialize(new[] { id }),
            });
            db.TemplateJobs.Add(new TemplateJob
            {
                Id = jobId, Kind = TemplateJobKind.Build, TemplateId = "built", MediaIds = JsonSerializer.Serialize(new[] { id }),
                State = TemplateJobState.Installing, RequestedByUserId = userId, CreatedAt = now,
            });
            await db.SaveChangesAsync(Ct);
        }

        var m = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/media/{id}", Ct);

        Assert.Equal(["Built v1", $"job {jobId}"], m.GetProperty("usedBy").EnumerateArray().Select(u => u.GetString()));
        var delete = await admin.DeleteAsync($"/api/admin/media/{id}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Contains("used by the build of", (await delete.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Media_dto_has_used_by_refs_with_template_existence()
    {
        var f = Factory();
        using var admin = f.CreateClient(UserRole.Admin);
        var id = await SeedMediaAsync(f, "w.iso");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.Templates.Add(new Template { Id = "refd", DisplayName = "Referenced", CreatedAt = now });
            await db.SaveChangesAsync(Ct);
            db.TemplateVersions.Add(new TemplateVersion
            {
                Id = Guid.NewGuid(), TemplateId = "refd", Number = 2, Origin = TemplateVersionOrigin.Imported, FileStem = "refd-2", SealedAt = now,
                SourceMediaIds = JsonSerializer.Serialize(new[] { id }),
            });
            await db.SaveChangesAsync(Ct);
        }

        var m = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/media/{id}", Ct);

        Assert.Equal(["Referenced v2"], m.GetProperty("usedBy").EnumerateArray().Select(u => u.GetString()));
        var r = Assert.Single(m.GetProperty("usedByRefs").EnumerateArray());
        Assert.Equal("refd", r.GetProperty("templateId").GetString());
        Assert.Equal("Referenced", r.GetProperty("templateName").GetString());
        Assert.Equal(2, r.GetProperty("versionNumber").GetInt32());
        Assert.True(r.GetProperty("templateExists").GetBoolean());
    }

    [Fact]
    public async Task Delete_Unused_204_ThenGone_AndUnknown404()
    {
        var f = Factory();
        using var admin = f.CreateClient(UserRole.Admin);
        var id = await SeedMediaAsync(f, "w.iso", MediaStatus.Failed);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/media/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/media/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/media/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task EveryRoute_Analyst403()
    {
        var f = Factory();
        using var analyst = f.CreateClient(UserRole.Analyst);
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.GetAsync("/api/admin/media", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.GetAsync($"/api/admin/media/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsync("/api/admin/media/uploads", JsonContent.Create(new { fileName = "w.iso", size = 1, kind = "OsInstaller" }), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.SendAsync(Put(id, new byte[1], "0"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsync("/api/admin/media/fetch", JsonContent.Create(new { url = "https://example.test/a.iso", kind = "OsInstaller" }), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.DeleteAsync($"/api/admin/media/{id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task EveryRoute_Unauthenticated401()
    {
        var f = Factory(authMode: "Entra");
        using var anonymous = f.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/media", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/admin/media/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/admin/media/uploads", JsonContent.Create(new { fileName = "a", size = 1, kind = "OsInstaller" }), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Put(id, new byte[1], "0"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/admin/media/fetch", JsonContent.Create(new { url = "https://example.test/a.iso", kind = "OsInstaller" }), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"/api/admin/media/{id}", Ct)).StatusCode);
    }
}

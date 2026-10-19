using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

[Collection("sql")]
public sealed class ArtifactDownloadTests(SqlServerFixture sql) : IDisposable
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

    private static byte[] Unzip(byte[] zipBytes, string password, out ZipEntry entry)
    {
        using var zf = new ZipFile(new MemoryStream(zipBytes)) { Password = password };
        Assert.Equal(1, zf.Count);
        entry = zf[0];
        using var s = zf.GetInputStream(entry);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Artifacts_ListedAsDtos()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f, screenshots: 2);
        using var client = f.CreateClient(UserRole.Analyst);

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{seeded.TaskId}/artifacts", Ct);
        Assert.Equal(2, list.GetArrayLength());
        var first = list[0];
        Assert.Contains(seeded.ScreenshotIds, id => id == first.GetProperty("id").GetGuid());
        Assert.Equal("Screenshot", first.GetProperty("kind").GetString());
        Assert.Equal(64, first.GetProperty("sha256").GetString()!.Length);
        Assert.True(first.GetProperty("size").GetInt64() > 0);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{Guid.NewGuid()}/artifacts", Ct)).StatusCode);
    }

    [Fact]
    public async Task Download_IsEncryptedZip_PasswordInfected()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f, screenshots: 1);
        using var client = f.CreateClient(UserRole.Analyst);
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{seeded.TaskId}/artifacts", Ct);
        var art = list[0];
        var sha = art.GetProperty("sha256").GetString()!;

        var resp = await client.GetAsync($"/api/artifacts/{art.GetProperty("id").GetGuid()}/download", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/zip", resp.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"{sha}.zip", resp.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var zip = await resp.Content.ReadAsByteArrayAsync(Ct);

        var plain = Unzip(zip, "infected", out var entry);
        Assert.Equal(sha, entry.Name);
        Assert.True(entry.IsCrypted);
        Assert.Equal(256, entry.AESKeySize);
        Assert.Equal(11, plain.Length);
        Assert.Equal(0x89, plain[0]);

        // Without the right password the entry cannot be read.
        using var zf = new ZipFile(new MemoryStream(zip)) { Password = "wrong" };
        Assert.ThrowsAny<Exception>(() =>
        {
            using var s = zf.GetInputStream(zf[0]);
            s.CopyTo(Stream.Null);
        });

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/artifacts/{Guid.NewGuid()}/download", Ct)).StatusCode);
    }

    [Fact]
    public async Task SampleDownload_IsEncryptedZip_WithSampleBytes()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f);
        using var client = f.CreateClient(UserRole.Analyst);

        var resp = await client.GetAsync($"/api/tasks/{seeded.TaskId}/sample", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal($"{seeded.SampleSha256}.zip", resp.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var plain = Unzip(await resp.Content.ReadAsByteArrayAsync(Ct), "infected", out var entry);
        Assert.Equal(seeded.SampleSha256, entry.Name);
        Assert.Equal(seeded.SampleBytes, plain);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{Guid.NewGuid()}/sample", Ct)).StatusCode);
    }

    [Fact]
    public async Task Download_Audited()
    {
        var f = Factory();
        var seeded = await CompletedTaskSeed.SeedAsync(f, screenshots: 1);
        using var client = f.CreateClient(UserRole.Analyst, "dl-analyst");
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{seeded.TaskId}/artifacts", Ct);
        var artId = list[0].GetProperty("id").GetGuid();

        (await client.GetAsync($"/api/artifacts/{artId}/download", Ct)).EnsureSuccessStatusCode();

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var rows = await db.AuditLogs.AsNoTracking().Where(a => a.Action == "artifact.download").ToListAsync(Ct);
        var row = Assert.Single(rows);
        Assert.Equal(artId.ToString(), row.Target);
        Assert.NotNull(row.UserId);

        // The sample download is audited too.
        (await client.GetAsync($"/api/tasks/{seeded.TaskId}/sample", Ct)).EnsureSuccessStatusCode();
        Assert.Equal(2, await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "artifact.download", Ct));
    }
}

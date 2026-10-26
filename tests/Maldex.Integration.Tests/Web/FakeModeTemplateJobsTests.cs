using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Core.Media;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

/// <summary>
/// The dev app as composed (fake mode, workers on, no service replaced): a Build from a library ISO and then an Edit of the
/// built template both reach Published. Only file paths and timings are configured.
/// </summary>
[Collection("sql")]
public sealed class FakeModeTemplateJobsTests(SqlServerFixture sql) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Directory.CreateTempSubdirectory("maldex-fake-jobs-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private async Task<MaldexAppFactory> NewAppAsync()
    {
        // What ./build.sh agent and real-setup leave on disk, and a placeholder virtio ISO (fake mode never reads it).
        await File.WriteAllBytesAsync(P("Maldex.Agent.exe"), RandomNumberGenerator.GetBytes(4096), Ct);
        await File.WriteAllTextAsync(P("Maldex.Agent.version"), "1.0.0\n", Ct);
        await File.WriteAllBytesAsync(P("virtio-win.iso"), [], Ct);
        using (var key = ECDsa.Create())
        {
            using var cert = new CertificateRequest("CN=maldex-test-ca", key, HashAlgorithmName.SHA256)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            await File.WriteAllTextAsync(P("ca.pem"), cert.ExportCertificatePem(), Ct);
        }
        Directory.CreateDirectory(P("media"));

        return new MaldexAppFactory(sql, workers: true, settings: new Dictionary<string, string?>
        {
            ["Maldex:Templates:Jobs:AgentExePath"] = P("Maldex.Agent.exe"),
            ["Maldex:Templates:Jobs:AgentVersionPath"] = P("Maldex.Agent.version"),
            ["Maldex:Templates:Jobs:MitmCaPemPath"] = P("ca.pem"),
            ["Maldex:Templates:Jobs:PollInterval"] = "00:00:00.1",
            ["Maldex:Templates:Jobs:MinFreeGiB"] = "1",
            ["Maldex:Templates:Build:VirtioIsoPath"] = P("virtio-win.iso"),
            ["Maldex:Templates:Build:WorkDir"] = P("build"),
            ["Maldex:Templates:Build:JobLogDir"] = P("jobs"),
            ["Maldex:Templates:Build:DiskGiB"] = "1",
            ["Maldex:Templates:Build:ProvisionPollInterval"] = "00:00:00.1",
            ["Maldex:Templates:Build:BootKeyTaps"] = "1",
            ["Maldex:Templates:Build:BootKeyInterval"] = "00:00:00.01",
            ["Maldex:Real:Libvirt:BaseImageDir"] = P("base"),
            ["Maldex:Media:Root"] = P("media"),
        });
    }

    /// <summary>An inspected Windows 11 installer in the library, its file where the media store keeps it.</summary>
    private static async Task<Guid> SeedOsMediaAsync(MaldexAppFactory app, string mediaRoot)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var user = TestUsers.Create(UserRole.Admin, "Seeder");
        db.Users.Add(user);
        var sha = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var media = new MediaFile
        {
            Id = Guid.NewGuid(), Kind = MediaKind.OsInstaller, FileName = "Win11_English_x64.iso", Size = 10, ReceivedBytes = 10, Sha256 = sha,
            Source = MediaSource.Upload, Status = MediaStatus.Ready, AddedByUserId = user.Id, AddedAt = now, LastActivityAt = now,
            Contents = MediaContentsJson.Serialize(new MediaContents([new InstallerImage(6, "Windows 11 Pro", "Professional", "26100", "x64")], null)),
        };
        db.MediaFiles.Add(media);
        await db.SaveChangesAsync(Ct);
        await File.WriteAllBytesAsync(Path.Combine(mediaRoot, sha + ".iso"), [], Ct);
        return media.Id;
    }

    private static async Task<JsonElement> WaitForStateAsync(HttpClient client, Guid jobId, string state)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{jobId}", Ct);
            var now = job.GetProperty("state").GetString();
            if (now == state) return job;
            if (now is "Failed" or "Cancelled" || sw.Elapsed > TimeSpan.FromSeconds(90))
                Assert.Fail($"The job is {now} ({job.GetProperty("message")}; failure: {job.GetProperty("failureReason")}), never {state}.");
            await Task.Delay(100, Ct);
        }
    }

    private static async Task SealAndPublishAsync(HttpClient client, Guid jobId)
    {
        await WaitForStateAsync(client, jobId, "Configuring");
        var seal = await client.PostAsJsonAsync($"/api/admin/template-jobs/{jobId}/seal", new { note = "fake" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, seal.StatusCode);
        await WaitForStateAsync(client, jobId, "Published");
    }

    [Fact]
    public async Task FakeApp_BuildsATemplateFromAnIso_AndEditsIt_ToPublished()
    {
        using var app = await NewAppAsync();
        using var client = app.CreateClient(UserRole.Admin, "builder");
        var os = await SeedOsMediaAsync(app, P("media"));

        var build = await client.PostAsJsonAsync("/api/admin/templates/build",
            new { templateId = "win11-fake", displayName = "Windows 11 (fake)", osMediaId = os, publishOnSuccess = true }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, build.StatusCode);
        var buildId = (await build.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await SealAndPublishAsync(client, buildId);

        var edit = await client.PostAsJsonAsync("/api/admin/templates/win11-fake/edit", new { network = "None", publishOnSuccess = true }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, edit.StatusCode);
        var editId = (await edit.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await SealAndPublishAsync(client, editId);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var versions = await db.TemplateVersions.AsNoTracking().Where(v => v.TemplateId == "win11-fake").OrderBy(v => v.Number).ToListAsync(Ct);
        Assert.Equal([1, 2], versions.Select(v => v.Number));
        var expected = Maldex.Core.Agent.AgentIdentity.BuildIdOfFiles(P("Maldex.Agent.exe"), P("Maldex.Agent.version"));
        Assert.All(versions, v => Assert.Equal(expected, v.AgentVersion)); // what the fake guest reported: the agent on disk
        var template = await db.Templates.AsNoTracking().SingleAsync(t => t.Id == "win11-fake", Ct);
        Assert.Equal(versions[1].Id, template.CurrentVersionId);
    }
}

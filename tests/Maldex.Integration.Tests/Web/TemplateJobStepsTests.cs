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
/// The step history of a template job: one row per transition, written by <see cref="TemplateJob.TransitionTo"/> and carried in
/// the job's DTO with the requester's name. The fake-mode runs use the dev app as composed (workers on), as FakeModeTemplateJobsTests does.
/// </summary>
[Collection("sql")]
public sealed class TemplateJobStepsTests(SqlServerFixture sql) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Directory.CreateTempSubdirectory("maldex-job-steps-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private async Task<MaldexAppFactory> NewFakeAppAsync()
    {
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

    private static async Task<Guid> SeedOsMediaAsync(MaldexAppFactory app, string mediaRoot)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var user = TestUsers.Create(UserRole.Admin, "Seeder");
        db.Users.Add(user);
        var sha = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        db.MediaFiles.Add(new MediaFile
        {
            Id = Guid.NewGuid(), Kind = MediaKind.OsInstaller, FileName = "Win11_English_x64.iso", Size = 10, ReceivedBytes = 10, Sha256 = sha,
            Source = MediaSource.Upload, Status = MediaStatus.Ready, AddedByUserId = user.Id, AddedAt = now, LastActivityAt = now,
            Contents = MediaContentsJson.Serialize(new MediaContents([new InstallerImage(6, "Windows 11 Pro", "Professional", "26100", "x64")], null)),
        });
        await db.SaveChangesAsync(Ct);
        await File.WriteAllBytesAsync(Path.Combine(mediaRoot, sha + ".iso"), [], Ct);
        return db.MediaFiles.Local.Single().Id;
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

    private static async Task<JsonElement> SealAndPublishAsync(HttpClient client, Guid jobId)
    {
        await WaitForStateAsync(client, jobId, "Configuring");
        var seal = await client.PostAsJsonAsync($"/api/admin/template-jobs/{jobId}/seal", new { note = "fake" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, seal.StatusCode);
        return await WaitForStateAsync(client, jobId, "Published");
    }

    private static async Task<Guid> BuildAsync(HttpClient client, Guid os)
    {
        var build = await client.PostAsJsonAsync("/api/admin/templates/build",
            new { templateId = "win11-steps", displayName = "Windows 11 (steps)", osMediaId = os, publishOnSuccess = true }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, build.StatusCode);
        var dto = await build.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(0, dto.GetProperty("steps").GetArrayLength()); // a queued job has made no transition yet
        return dto.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Fake_edit_job_records_each_state_in_order()
    {
        using var app = await NewFakeAppAsync();
        using var client = app.CreateClient(UserRole.Admin, "builder");
        var os = await SeedOsMediaAsync(app, P("media"));
        await SealAndPublishAsync(client, await BuildAsync(client, os));

        var edit = await client.PostAsJsonAsync("/api/admin/templates/win11-steps/edit", new { network = "None", publishOnSuccess = true }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, edit.StatusCode);
        var editId = (await edit.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var published = await SealAndPublishAsync(client, editId);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var steps = await db.TemplateJobSteps.AsNoTracking().Where(s => s.JobId == editId).OrderBy(s => s.Id).ToListAsync(Ct);
        TemplateJobState[] path = [TemplateJobState.Preparing, TemplateJobState.Configuring, TemplateJobState.Sealing, TemplateJobState.Verifying, TemplateJobState.Published];
        Assert.Equal(path, steps.Select(s => s.State));
        for (var i = 1; i < steps.Count; i++) Assert.True(steps[i].At >= steps[i - 1].At, $"step {i} is earlier than step {i - 1}");
        var job = await db.TemplateJobs.AsNoTracking().SingleAsync(j => j.Id == editId, Ct);
        Assert.Equal(job.Message, steps[^1].Message);

        // The DTO carries the same timeline, in order.
        var dtoSteps = published.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(path.Select(s => s.ToString()), dtoSteps.Select(s => s.GetProperty("state").GetString()));
        Assert.Equal(steps.Select(s => s.Message), dtoSteps.Select(s => s.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Job_dto_carries_steps_and_requester_name()
    {
        using var app = await NewFakeAppAsync();
        using var client = app.CreateClient(UserRole.Admin, "builder");
        var os = await SeedOsMediaAsync(app, P("media"));
        var buildId = await BuildAsync(client, os);
        await SealAndPublishAsync(client, buildId);

        var job = await client.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{buildId}", Ct);
        var steps = job.GetProperty("steps").EnumerateArray().ToList();
        Assert.True(steps.Count > 0);
        Assert.Equal("Preparing", steps[0].GetProperty("state").GetString());
        Assert.Equal("Published", steps[^1].GetProperty("state").GetString());
        Assert.All(steps, s => Assert.True(s.TryGetProperty("at", out _)));
        Assert.Equal("builder", job.GetProperty("requestedByName").GetString());

        // The template's job list carries them too.
        var list = await client.GetFromJsonAsync<JsonElement>("/api/admin/templates/win11-steps/jobs", Ct);
        var listed = list.EnumerateArray().Single(j => j.GetProperty("id").GetGuid() == buildId);
        Assert.Equal(steps.Count, listed.GetProperty("steps").GetArrayLength());
        Assert.Equal("builder", listed.GetProperty("requestedByName").GetString());
    }

    /// <summary>A template with no versions and one terminal job (with <paramref name="steps"/> transitions), inserted directly.</summary>
    private static async Task<(string TemplateId, Guid JobId)> SeedFailedBuildAsync(MaldexAppFactory app, string templateId, bool steps)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var now = DateTimeOffset.UtcNow;
        var user = TestUsers.Create(UserRole.Admin, "Old Requester");
        db.Users.Add(user);
        db.Templates.Add(new Template { Id = templateId, DisplayName = templateId, CreatedAt = now });
        var job = new TemplateJob
        {
            Id = Guid.NewGuid(), Kind = TemplateJobKind.Build, TemplateId = templateId, Network = NetworkMode.None,
            RequestedByUserId = user.Id, CreatedAt = now,
        };
        if (steps)
        {
            job.TransitionTo(TemplateJobState.Preparing, "starting", now);
            job.TransitionTo(TemplateJobState.Failed, "boom", now.AddSeconds(1));
        }
        else
        {
            job.State = TemplateJobState.Failed; // a job from before the step history: no step rows
            job.EndedAt = now;
        }
        db.TemplateJobs.Add(job);
        await db.SaveChangesAsync(Ct);
        return (templateId, job.Id);
    }

    [Fact]
    public async Task Job_without_steps_returns_an_empty_list()
    {
        using var app = new MaldexAppFactory(sql);
        using var client = app.CreateClient(UserRole.Admin);
        var (_, jobId) = await SeedFailedBuildAsync(app, "steps-none", steps: false);

        var job = await client.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{jobId}", Ct);
        Assert.Equal(JsonValueKind.Array, job.GetProperty("steps").ValueKind);
        Assert.Equal(0, job.GetProperty("steps").GetArrayLength());
        Assert.Equal("Old Requester", job.GetProperty("requestedByName").GetString());
    }

    [Fact]
    public async Task Deleting_a_template_deletes_its_job_steps()
    {
        using var app = new MaldexAppFactory(sql);
        using var client = app.CreateClient(UserRole.Admin);
        var (templateId, jobId) = await SeedFailedBuildAsync(app, "steps-delete", steps: true);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            Assert.Equal(2, await db.TemplateJobSteps.CountAsync(s => s.JobId == jobId, Ct));
        }

        var delete = await client.DeleteAsync($"/api/admin/templates/{templateId}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            Assert.False(await db.TemplateJobs.AnyAsync(j => j.Id == jobId, Ct));
            Assert.Equal(0, await db.TemplateJobSteps.CountAsync(s => s.JobId == jobId, Ct));
        }
    }
}

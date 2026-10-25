using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Media;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Maldex.Integration.Tests.Web;

/// <summary>The Build endpoint, a Build job's DTO and provisioning log, and deleting an empty template (workers off: no VM runs).</summary>
[Collection("sql")]
public sealed class TemplateBuildEndpointsTests(SqlServerFixture sql) : IDisposable
{
    private const string Seeded = "win10-ltsc-2021"; // imported at startup: default, Ready v1

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<IDisposable> _disposables = [];
    private readonly string _logRoot = Path.Combine(Path.GetTempPath(), "maldex-build-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        if (Directory.Exists(_logRoot)) Directory.Delete(_logRoot, true);
    }

    private sealed class SettableDisk : IDiskSpaceProbe
    {
        public long Mib { get; set; } = 500 * 1024;
        public long FreeMiB(string path) => Mib;
    }

    private sealed class Rig
    {
        public required MaldexAppFactory Factory { get; init; }
        public required SettableDisk Disk { get; init; }
        public required string LogRoot { get; init; }
        public HttpClient Admin(string user = "builder") => Factory.CreateClient(UserRole.Admin, user);
    }

    private sealed class BuildFactory(SqlServerFixture sql, SettableDisk disk, string logRoot) : MaldexAppFactory(sql, settings: new Dictionary<string, string?>
    {
        ["Maldex:Templates:Build:JobLogDir"] = logRoot,
    })
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IDiskSpaceProbe>(disk)));
        }
    }

    private Rig NewRig()
    {
        var disk = new SettableDisk();
        var f = new BuildFactory(sql, disk, _logRoot);
        _disposables.Add(f);
        return new Rig { Factory = f, Disk = disk, LogRoot = _logRoot };
    }

    private static InstallerImage Image(int index, string name, string arch = "x86_64") => new(index, name, null, "26100", arch);

    private static readonly InstallerImage[] ProAndHome = [Image(1, "Windows 11 Home"), Image(2, "Windows 11 Pro")];

    private static async Task<T> InDbAsync<T>(Rig r, Func<MaldexDbContext, Task<T>> read)
    {
        await using var scope = r.Factory.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<MaldexDbContext>());
    }

    private static async Task<Guid> SeedMediaAsync(Rig r, MediaKind kind = MediaKind.OsInstaller, MediaStatus status = MediaStatus.Ready,
        IReadOnlyList<InstallerImage>? images = null, string? application = null, bool inspected = true)
    {
        await using var scope = r.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var user = new AppUser { Id = Guid.NewGuid(), EntraObjectId = Guid.NewGuid().ToString("N"), DisplayName = "Seeder", Role = UserRole.Admin };
        db.Users.Add(user);
        var now = DateTimeOffset.UtcNow;
        var media = new MediaFile
        {
            Id = Guid.NewGuid(), Kind = kind, FileName = kind == MediaKind.OsInstaller ? "win11.iso" : "office.iso", Size = 100, ReceivedBytes = 100,
            Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), Source = MediaSource.Upload, Status = status,
            AddedByUserId = user.Id, AddedAt = now, LastActivityAt = now,
            Contents = inspected ? MediaContentsJson.Serialize(new MediaContents(images ?? (kind == MediaKind.OsInstaller ? ProAndHome : []), application)) : null,
        };
        db.MediaFiles.Add(media);
        await db.SaveChangesAsync(Ct);
        return media.Id;
    }

    private static Task<HttpResponseMessage> BuildAsync(HttpClient client, Guid os, string templateId = "win11-pro", string? displayName = "Windows 11 Pro",
        int? editionIndex = null, Guid[]? apps = null, bool? publish = null) =>
        client.PostAsJsonAsync("/api/admin/templates/build",
            new { templateId, displayName, osMediaId = os, editionIndex, applicationMediaIds = apps, publishOnSuccess = publish }, Ct);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage resp) => await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task AssertProblemAsync(HttpResponseMessage resp, HttpStatusCode status, string title)
    {
        Assert.Equal(status, resp.StatusCode);
        Assert.Equal(title, (await ReadAsync(resp)).GetProperty("title").GetString());
    }

    private static Task<List<AuditLog>> AuditsAsync(Rig r, string action) =>
        InDbAsync(r, db => db.AuditLogs.AsNoTracking().Where(a => a.Action == action).OrderBy(a => a.Id).ToListAsync(Ct));

    private static async Task<Guid> SeedJobAsync(Rig r, string templateId, TemplateJobState state, TemplateJobKind kind = TemplateJobKind.Build, DateTimeOffset? sealRequestedAt = null)
    {
        using var client = r.Admin();
        await client.GetAsync("/api/me", Ct); // creates the dev user row
        return await InDbAsync(r, async db =>
        {
            var user = await db.Users.AsNoTracking().FirstAsync(Ct);
            var baseId = kind == TemplateJobKind.Build ? null : await db.Templates.AsNoTracking().Where(t => t.Id == templateId).Select(t => t.CurrentVersionId).SingleAsync(Ct);
            var terminal = Maldex.Core.Lifecycle.TemplateJobStateMachine.IsTerminal(state);
            var job = new TemplateJob
            {
                Id = Guid.NewGuid(), Kind = kind, TemplateId = templateId, BaseVersionId = baseId, Network = NetworkMode.None, State = state, RequestedByUserId = user.Id,
                CreatedAt = DateTimeOffset.UtcNow, EndedAt = terminal ? DateTimeOffset.UtcNow : null, SealRequestedAt = sealRequestedAt,
            };
            db.TemplateJobs.Add(job);
            await db.SaveChangesAsync(Ct);
            return job.Id;
        });
    }

    // ---- build: happy paths ----

    [Fact]
    public async Task Build_NewTemplate_Returns202_CreatesTemplateAndJob_AndAudits()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        using var client = r.Admin("builder");

        var resp = await BuildAsync(client, os, publish: false);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var dto = await ReadAsync(resp);
        var id = dto.GetProperty("id").GetGuid();
        Assert.Equal($"/api/admin/template-jobs/{id}", resp.Headers.Location!.ToString());
        Assert.Equal("Build", dto.GetProperty("kind").GetString());
        Assert.Equal("win11-pro", dto.GetProperty("templateId").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("baseVersionNumber").ValueKind);
        Assert.Equal("None", dto.GetProperty("network").GetString());
        Assert.Equal("Queued", dto.GetProperty("state").GetString());
        Assert.Equal("Windows 11 Pro", dto.GetProperty("edition").GetString());
        Assert.False(dto.GetProperty("hasProvisionLog").GetBoolean());
        Assert.False(dto.GetProperty("sealRequested").GetBoolean());

        var (template, job) = await InDbAsync(r, async db => (await db.Templates.AsNoTracking().SingleAsync(t => t.Id == "win11-pro", Ct),
            await db.TemplateJobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct)));
        Assert.Equal("Windows 11 Pro", template.DisplayName);
        Assert.True(template.Enabled);
        Assert.False(template.IsDefault);
        Assert.Null(template.CurrentVersionId);
        Assert.Equal(TemplateJobKind.Build, job.Kind);
        Assert.Null(job.BaseVersionId);
        Assert.Null(job.ExpectedCurrentVersionId);
        Assert.Equal(NetworkMode.None, job.Network);
        Assert.False(job.PublishOnSuccess);
        Assert.Equal(2, job.EditionIndex);
        Assert.Equal("Windows 11 Pro", job.EditionName);
        Assert.Equal([os], JsonSerializer.Deserialize<List<Guid>>(job.MediaIds));

        var create = Assert.Single(await AuditsAsync(r, "template.create"));
        Assert.Equal("win11-pro", create.Target);
        var start = Assert.Single(await AuditsAsync(r, "template.job.start"));
        Assert.Equal("win11-pro", start.Target);
        Assert.Equal($"job={id}; kind=Build; network=None; base=none; publish=False; media={os}; edition=Windows 11 Pro", start.Details);
    }

    [Fact]
    public async Task Build_ExistingTemplate_RecordsItsCurrentVersion_AndDoesNotCreateAgain_MediaOsFirstThenOffice()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        var office = await SeedMediaAsync(r, MediaKind.Application, application: "Office");
        var current = await InDbAsync(r, db => db.Templates.AsNoTracking().Where(t => t.Id == Seeded).Select(t => t.CurrentVersionId).SingleAsync(Ct));
        using var client = r.Admin();

        // The id is found case-insensitively and stored as it is in the database; no display name is needed.
        var resp = await BuildAsync(client, os, "WIN10-LTSC-2021", displayName: null, editionIndex: 2, apps: [office]);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var id = (await ReadAsync(resp)).GetProperty("id").GetGuid();
        var job = await InDbAsync(r, db => db.TemplateJobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct));
        Assert.Equal(Seeded, job.TemplateId);
        Assert.NotNull(current);
        Assert.Equal(current, job.ExpectedCurrentVersionId);
        Assert.True(job.PublishOnSuccess);
        Assert.Equal([os, office], JsonSerializer.Deserialize<List<Guid>>(job.MediaIds));
        Assert.Empty(await AuditsAsync(r, "template.create"));
        Assert.Single(await AuditsAsync(r, "template.job.start"));
    }

    // ---- build: 400s ----

    [Theory]
    [InlineData("Bad Slug!")]
    [InlineData("-leading-dash")]
    [InlineData("")]
    [InlineData("has_underscore")]
    public async Task Build_BadTemplateId_Is400(string templateId)
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        using var client = r.Admin();

        await AssertProblemAsync(await BuildAsync(client, os, templateId), HttpStatusCode.BadRequest,
            "templateId must be 1 to 64 lowercase letters, digits or hyphens, starting with a letter or digit");
        Assert.Empty(await InDbAsync(r, db => db.TemplateJobs.AsNoTracking().Where(j => j.Kind == TemplateJobKind.Build).ToListAsync(Ct)));
    }

    [Fact]
    public async Task Build_SlugOf65Characters_Is400()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        using var client = r.Admin();

        var resp = await BuildAsync(client, os, new string('a', 65));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Build_MediaThatIsNotInstallerMedia_Is400()
    {
        var r = NewRig();
        var app = await SeedMediaAsync(r, MediaKind.Application, application: "Office");
        using var client = r.Admin();

        await AssertProblemAsync(await BuildAsync(client, app), HttpStatusCode.BadRequest, "office.iso is not installer media");
    }

    [Fact]
    public async Task Build_UnknownInstallerMedia_Is400()
    {
        var r = NewRig();
        using var client = r.Admin();

        await AssertProblemAsync(await BuildAsync(client, Guid.NewGuid()), HttpStatusCode.BadRequest, "the installer media was not found");
    }

    [Fact]
    public async Task Build_FailedInstallerMedia_Is400()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r, status: MediaStatus.Failed);
        using var client = r.Admin();

        await AssertProblemAsync(await BuildAsync(client, os), HttpStatusCode.BadRequest, "the installer media win11.iso is Failed");
    }

    [Fact]
    public async Task Build_UninspectedInstallerMedia_Is400()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r, inspected: false);
        using var client = r.Admin();

        await AssertProblemAsync(await BuildAsync(client, os), HttpStatusCode.BadRequest, "the installer media win11.iso has not been inspected");
    }

    [Fact]
    public async Task Build_HomeEditionIndex_IsRefusedWithTheSelectorsMessage()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        using var client = r.Admin();

        var resp = await BuildAsync(client, os, editionIndex: 1);

        await AssertProblemAsync(resp, HttpStatusCode.BadRequest, EditionSelector.Unsupported(ProAndHome[0])!);
    }

    [Fact]
    public async Task Build_MediaWithNoSupportedEdition_IsRefusedWithTheSelectorsMessage()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r, images: [Image(1, "Windows 11 Home")]);
        using var client = r.Admin();

        await AssertProblemAsync(await BuildAsync(client, os), HttpStatusCode.BadRequest, "no Enterprise or Pro image on this media");
    }

    [Fact]
    public async Task Build_TwoOfficeMedia_Is400()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        var a = await SeedMediaAsync(r, MediaKind.Application, application: "Office");
        var b = await SeedMediaAsync(r, MediaKind.Application, application: "Office");
        using var client = r.Admin();

        await AssertProblemAsync(await BuildAsync(client, os, apps: [a, b]), HttpStatusCode.BadRequest, "only Office media can be installed by a build");
    }

    [Fact]
    public async Task Build_ApplicationMediaThatIsNotOffice_Is400()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        var other = await SeedMediaAsync(r, MediaKind.Application, application: "7-Zip");
        var unrecognised = await SeedMediaAsync(r, MediaKind.Application, application: null);
        using var client = r.Admin();

        await AssertProblemAsync(await BuildAsync(client, os, apps: [other]), HttpStatusCode.BadRequest, "only Office media can be installed by a build");
        await AssertProblemAsync(await BuildAsync(client, os, apps: [unrecognised]), HttpStatusCode.BadRequest, "only Office media can be installed by a build");
        await AssertProblemAsync(await BuildAsync(client, os, apps: [Guid.NewGuid()]), HttpStatusCode.BadRequest, "only Office media can be installed by a build");
        await AssertProblemAsync(await BuildAsync(client, os, apps: [os]), HttpStatusCode.BadRequest, "only Office media can be installed by a build");
    }

    [Fact]
    public async Task Build_FailedOfficeMedia_Is400()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        var office = await SeedMediaAsync(r, MediaKind.Application, MediaStatus.Failed, application: "Office");
        using var client = r.Admin();

        await AssertProblemAsync(await BuildAsync(client, os, apps: [office]), HttpStatusCode.BadRequest, "the application media office.iso is Failed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Build_NewTemplateWithoutDisplayName_Is400(string? displayName)
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        using var client = r.Admin();

        await AssertProblemAsync(await BuildAsync(client, os, displayName: displayName), HttpStatusCode.BadRequest,
            "displayName is required for a new template (1 to 100 characters)");
        Assert.Empty(await InDbAsync(r, db => db.Templates.AsNoTracking().Where(t => t.Id == "win11-pro").ToListAsync(Ct)));
    }

    [Fact]
    public async Task Build_DisplayNameOver100Characters_Is400()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        using var client = r.Admin();

        var resp = await BuildAsync(client, os, displayName: new string('x', 101));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- build: 409s and access ----

    [Fact]
    public async Task Build_WhileAJobIsActive_Is409_AndWritesNothing()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        await SeedJobAsync(r, Seeded, TemplateJobState.Configuring, TemplateJobKind.Edit);
        using var client = r.Admin();

        var resp = await BuildAsync(client, os, Seeded, displayName: null);

        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "a job is already running for this template");
        Assert.Empty(await AuditsAsync(r, "template.job.start"));
    }

    [Fact]
    public async Task Build_WithLowDiskSpace_Is409_AndCreatesNoTemplate()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        r.Disk.Mib = 10 * 1024; // a Build needs DiskGiB (64) + MinFreeGiB
        using var client = r.Admin();

        var resp = await BuildAsync(client, os);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.StartsWith("not enough disk space", (await ReadAsync(resp)).GetProperty("title").GetString());
        Assert.Empty(await InDbAsync(r, db => db.Templates.AsNoTracking().Where(t => t.Id == "win11-pro").ToListAsync(Ct)));
        Assert.Empty(await AuditsAsync(r, "template.create"));
    }

    [Fact]
    public async Task Build_SecondBuildOfTheSameNewTemplate_Is409()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        using var client = r.Admin();

        Assert.Equal(HttpStatusCode.Accepted, (await BuildAsync(client, os)).StatusCode);
        var second = await BuildAsync(client, os, displayName: null);

        await AssertProblemAsync(second, HttpStatusCode.Conflict, "a job is already running for this template");
    }

    [Fact]
    public async Task NewRoutes_AreAdminOnly()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        var job = await SeedJobAsync(r, Seeded, TemplateJobState.Failed);
        using var analyst = r.Factory.CreateClient(UserRole.Analyst, "analyst");

        Assert.Equal(HttpStatusCode.Forbidden, (await BuildAsync(analyst, os)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.DeleteAsync($"/api/admin/templates/{Seeded}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.GetAsync($"/api/admin/template-jobs/{job}/provision-log", Ct)).StatusCode);
        Assert.Empty(await InDbAsync(r, db => db.Templates.AsNoTracking().Where(t => t.Id == "win11-pro").ToListAsync(Ct)));
    }

    // ---- the DTO ----

    [Fact]
    public async Task JobDto_ShowsSealRequested_AndNullBaseForABuild()
    {
        var r = NewRig();
        var asked = await SeedJobAsync(r, Seeded, TemplateJobState.Configuring, sealRequestedAt: DateTimeOffset.UtcNow);
        await InDbAsync(r, async db => await db.TemplateJobs.Where(j => j.Id == asked).ExecuteUpdateAsync(s => s.SetProperty(j => j.EditionName, "Windows 11 Pro"), Ct));
        using var client = r.Admin();

        var dto = await client.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{asked}", Ct);

        Assert.True(dto.GetProperty("sealRequested").GetBoolean());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("baseVersionNumber").ValueKind);
        Assert.Equal("Windows 11 Pro", dto.GetProperty("edition").GetString());

        // The Defender refusal clears the request: the DTO says so, so the UI offers Seal again.
        await InDbAsync(r, async db => await db.TemplateJobs.Where(j => j.Id == asked).ExecuteUpdateAsync(s => s.SetProperty(j => j.SealRequestedAt, (DateTimeOffset?)null), Ct));
        dto = await client.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{asked}", Ct);
        Assert.False(dto.GetProperty("sealRequested").GetBoolean());
    }

    [Fact]
    public async Task JobDto_HasProvisionLog_FollowsTheFile_AndTheTemplateJobsListUsesIt()
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, Seeded, TemplateJobState.Failed);
        using var client = r.Admin();
        Assert.False((await client.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{id}", Ct)).GetProperty("hasProvisionLog").GetBoolean());

        Directory.CreateDirectory(Path.Combine(r.LogRoot, id.ToString("N")));
        await File.WriteAllTextAsync(Path.Combine(r.LogRoot, id.ToString("N"), "provision.log"), "step 1 ok\n", Ct);

        Assert.True((await client.GetFromJsonAsync<JsonElement>($"/api/admin/template-jobs/{id}", Ct)).GetProperty("hasProvisionLog").GetBoolean());
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/admin/templates/{Seeded}/jobs", Ct);
        Assert.True(list.EnumerateArray().Single(j => j.GetProperty("id").GetGuid() == id).GetProperty("hasProvisionLog").GetBoolean());
    }

    // ---- provisioning log ----

    [Fact]
    public async Task ProvisionLog_IsADownloadOfThePlainText_WithNosniff()
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, Seeded, TemplateJobState.Failed);
        Directory.CreateDirectory(Path.Combine(r.LogRoot, id.ToString("N")));
        await File.WriteAllTextAsync(Path.Combine(r.LogRoot, id.ToString("N"), "provision.log"), "<script>alert(1)</script>\nstep 1 ok\n", Ct);
        using var client = r.Admin();

        var resp = await client.GetAsync($"/api/admin/template-jobs/{id}/provision-log", Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", resp.Content.Headers.ContentType!.ToString());
        Assert.Equal("attachment", resp.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", Assert.Single(resp.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("<script>alert(1)</script>\nstep 1 ok\n", await resp.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ProvisionLog_Is404_WhenThereIsNoLogOrNoJob()
    {
        var r = NewRig();
        var id = await SeedJobAsync(r, Seeded, TemplateJobState.Failed);
        using var client = r.Admin();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/template-jobs/{id}/provision-log", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/template-jobs/{Guid.NewGuid()}/provision-log", Ct)).StatusCode);
    }

    // ---- the admin list and deleting an empty template ----

    [Fact]
    public async Task AdminList_ShowsATemplateWithNoVersion_AndAnalystsDoNotSeeIt()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        using var admin = r.Admin();
        Assert.Equal(HttpStatusCode.Accepted, (await BuildAsync(admin, os)).StatusCode);

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/admin/templates", Ct);
        var row = list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == "win11-pro");
        Assert.Equal(JsonValueKind.Null, row.GetProperty("current").ValueKind);
        Assert.Empty(row.GetProperty("versions").EnumerateArray());

        using var analyst = r.Factory.CreateClient(UserRole.Analyst, "analyst");
        var visible = await analyst.GetFromJsonAsync<JsonElement>("/api/templates", Ct);
        Assert.DoesNotContain(visible.EnumerateArray(), t => t.GetProperty("id").GetString() == "win11-pro");
    }

    [Fact]
    public async Task DeleteEmptyTemplate_Returns204_RemovesItAndItsJobs_AndAudits()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        using var client = r.Admin("deleter");
        var id = (await ReadAsync(await BuildAsync(client, os))).GetProperty("id").GetGuid();
        await InDbAsync(r, async db => await db.TemplateJobs.Where(j => j.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, TemplateJobState.Failed).SetProperty(j => j.EndedAt, DateTimeOffset.UtcNow), Ct));

        var resp = await client.DeleteAsync("/api/admin/templates/WIN11-PRO", Ct); // case-insensitive

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(await InDbAsync(r, db => db.Templates.AsNoTracking().Where(t => t.Id == "win11-pro").ToListAsync(Ct)));
        Assert.Empty(await InDbAsync(r, db => db.TemplateJobs.AsNoTracking().Where(j => j.TemplateId == "win11-pro").ToListAsync(Ct)));
        var audit = Assert.Single(await AuditsAsync(r, "template.delete"));
        Assert.Equal("win11-pro", audit.Target);
        Assert.NotNull(audit.UserId);
        Assert.Single(await AuditsAsync(r, "template.create")); // the history stays
        Assert.Single(await AuditsAsync(r, "template.job.start"));
    }

    [Fact]
    public async Task DeleteEmptyTemplate_WithAVersion_Is409()
    {
        var r = NewRig();
        using var client = r.Admin();
        await InDbAsync(r, async db =>
        {
            db.Templates.Add(new Template { Id = "has-retired", DisplayName = "Has retired", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
            db.TemplateVersions.Add(new TemplateVersion
            {
                Id = Guid.NewGuid(), TemplateId = "has-retired", Number = 1, Origin = TemplateVersionOrigin.IsoBuild, FileStem = "has-retired",
                SealedAt = DateTimeOffset.UtcNow, Status = TemplateVersionStatus.Deleted, // any status counts
            });
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        var resp = await client.DeleteAsync("/api/admin/templates/has-retired", Ct);

        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "the template has versions; it cannot be deleted");
        Assert.Single(await InDbAsync(r, db => db.Templates.AsNoTracking().Where(t => t.Id == "has-retired").ToListAsync(Ct)));
        Assert.Empty(await AuditsAsync(r, "template.delete"));
    }

    [Fact]
    public async Task DeleteEmptyTemplate_WithAnActiveJob_Is409()
    {
        var r = NewRig();
        var os = await SeedMediaAsync(r);
        using var client = r.Admin();
        Assert.Equal(HttpStatusCode.Accepted, (await BuildAsync(client, os)).StatusCode); // Queued

        var resp = await client.DeleteAsync("/api/admin/templates/win11-pro", Ct);

        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "a job is already running for this template");
        Assert.Single(await InDbAsync(r, db => db.Templates.AsNoTracking().Where(t => t.Id == "win11-pro").ToListAsync(Ct)));
    }

    [Fact]
    public async Task DeleteEmptyTemplate_ThatIsTheDefault_Is409()
    {
        var r = NewRig();
        using var client = r.Admin();
        await InDbAsync(r, async db =>
        {
            db.Templates.Add(new Template { Id = "empty-default", DisplayName = "Empty default", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
            return 0;
        });
        // The seeded default must give way first (the unique index allows one default).
        await InDbAsync(r, async db =>
        {
            await db.Templates.Where(t => t.Id == Seeded).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsDefault, false), Ct);
            await db.Templates.Where(t => t.Id == "empty-default").ExecuteUpdateAsync(s => s.SetProperty(t => t.IsDefault, true), Ct);
            return 0;
        });

        var resp = await client.DeleteAsync("/api/admin/templates/empty-default", Ct);

        await AssertProblemAsync(resp, HttpStatusCode.Conflict, "choose another default first");
        Assert.Single(await InDbAsync(r, db => db.Templates.AsNoTracking().Where(t => t.Id == "empty-default").ToListAsync(Ct)));
    }

    [Fact]
    public async Task DeleteEmptyTemplate_Unknown_Is404()
    {
        var r = NewRig();
        using var client = r.Admin();

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/admin/templates/nope", Ct)).StatusCode);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

/// <summary>The usage fields of the admin template list and the template detail endpoint.</summary>
[Collection("sql")]
public sealed class TemplateDetailTests(SqlServerFixture sql) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    private MaldexAppFactory Factory(Dictionary<string, string?>? settings = null)
    {
        var f = new MaldexAppFactory(sql, settings: settings);
        _disposables.Add(f);
        return f;
    }

    private sealed record Seeded(Guid V1, Guid V2, Guid Media, Guid DeletedMedia, Guid UserId);

    /// <summary>Template "t1": v1 sealed 40 days ago, v2 (current, child of v1) sealed <paramref name="v2AgeDays"/> days ago.</summary>
    private static async Task<Seeded> SeedAsync(MaldexAppFactory f, int v2AgeDays, bool withVersions = true)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var now = DateTimeOffset.UtcNow;
        var user = new AppUser { Id = Guid.NewGuid(), EntraObjectId = Guid.NewGuid().ToString("N"), DisplayName = "Builder", Role = UserRole.Admin };
        var media = new MediaFile
        {
            Id = Guid.NewGuid(), Kind = MediaKind.OsInstaller, FileName = "Win11.iso", Size = 1, ReceivedBytes = 1, Sha256 = new string('a', 64),
            Source = MediaSource.Upload, Status = MediaStatus.Ready, AddedByUserId = user.Id, AddedAt = now, LastActivityAt = now,
        };
        var deleted = Guid.NewGuid();
        db.AddRange(user, media);
        db.Templates.Add(new Template { Id = "t1", DisplayName = "T1", CreatedAt = now });
        await db.SaveChangesAsync(Ct);
        if (!withVersions) return new Seeded(Guid.Empty, Guid.Empty, media.Id, deleted, user.Id);

        var v1 = new TemplateVersion
        {
            Id = Guid.NewGuid(), TemplateId = "t1", Number = 1, Origin = TemplateVersionOrigin.IsoBuild, FileStem = "t1-1",
            SealedAt = now.AddDays(-40), CurrentUntil = now.AddDays(-v2AgeDays),
        };
        var v2 = new TemplateVersion
        {
            Id = Guid.NewGuid(), TemplateId = "t1", Number = 2, Origin = TemplateVersionOrigin.Edit, FileStem = "t1-2",
            SealedAt = now.AddDays(-v2AgeDays), ParentVersionId = v1.Id, SysmonConfigSha256 = new string('b', 64), MitmCaSha256 = new string('c', 64),
            SourceMediaIds = $"[\"{media.Id}\",\"{deleted}\"]",
        };
        db.TemplateVersions.AddRange(v1, v2);
        await db.SaveChangesAsync(Ct);
        (await db.Templates.SingleAsync(t => t.Id == "t1", Ct)).CurrentVersionId = v2.Id;
        await db.SaveChangesAsync(Ct);
        return new Seeded(v1.Id, v2.Id, media.Id, deleted, user.Id);
    }

    private static async Task AddTasksAsync(MaldexAppFactory f, Guid userId, Guid versionId, params int[] ageDays)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var org = new Maldex.Core.Domain.Org { Id = Guid.NewGuid(), Name = "Org-" + Guid.NewGuid().ToString("N"), RetentionDays = 30 };
        db.Add(org);
        foreach (var age in ageDays)
            db.Tasks.Add(new AnalysisTask
            {
                Id = Guid.NewGuid(), OrgId = org.Id, SubmittedByUserId = userId, Url = "http://example.test/", Image = "t1", TemplateVersionId = versionId,
                NetworkMode = NetworkMode.None, DurationSec = 60, CreatedAt = DateTimeOffset.UtcNow.AddDays(-age), Status = RunStatus.Completed,
            });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task AddJobAsync(MaldexAppFactory f, Guid userId, TemplateJobState state, int ageMinutes)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        db.TemplateJobs.Add(new TemplateJob
        {
            Id = Guid.NewGuid(), Kind = TemplateJobKind.Build, TemplateId = "t1", Network = NetworkMode.None, State = state,
            RequestedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-ageMinutes),
            EndedAt = state is TemplateJobState.Failed or TemplateJobState.Cancelled ? DateTimeOffset.UtcNow : null,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<JsonElement> ListedAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/api/admin/templates", Ct);
        return list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == "t1");
    }

    [Fact]
    public async Task List_has_task_counts_refresh_due_and_active_job()
    {
        var f = Factory();
        var s = await SeedAsync(f, v2AgeDays: 31);
        await AddTasksAsync(f, s.UserId, s.V2, 1, 5);
        await AddTasksAsync(f, s.UserId, s.V1, 45);
        await AddJobAsync(f, s.UserId, TemplateJobState.Failed, 30);
        using var client = f.CreateClient(UserRole.Admin);

        var t = await ListedAsync(client);
        Assert.True(t.GetProperty("refreshDue").GetBoolean());
        Assert.Equal(2, t.GetProperty("tasksLast30Days").GetInt32());
        Assert.Equal(JsonValueKind.Null, t.GetProperty("activeJobId").ValueKind);

        await AddJobAsync(f, s.UserId, TemplateJobState.Configuring, 1);
        t = await ListedAsync(client);
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var configuring = await db.TemplateJobs.AsNoTracking().SingleAsync(j => j.State == TemplateJobState.Configuring, Ct);
        Assert.Equal(configuring.Id, t.GetProperty("activeJobId").GetGuid());
    }

    [Fact]
    public async Task Recent_version_is_not_refresh_due()
    {
        var f = Factory();
        await SeedAsync(f, v2AgeDays: 29);
        using var client = f.CreateClient(UserRole.Admin);
        Assert.False((await ListedAsync(client)).GetProperty("refreshDue").GetBoolean());
    }

    [Fact]
    public async Task RefreshDueDays_option_changes_the_threshold()
    {
        var f = Factory(new() { ["Maldex:Templates:RefreshDueDays"] = "7" });
        await SeedAsync(f, v2AgeDays: 10);
        using var client = f.CreateClient(UserRole.Admin);
        Assert.True((await ListedAsync(client)).GetProperty("refreshDue").GetBoolean());
    }

    [Fact]
    public async Task Detail_returns_versions_with_provenance_media_and_usage()
    {
        var f = Factory();
        var s = await SeedAsync(f, v2AgeDays: 2);
        await AddTasksAsync(f, s.UserId, s.V2, 1, 2, 3);
        await AddTasksAsync(f, s.UserId, s.V1, 50);
        using var client = f.CreateClient(UserRole.Admin);

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/admin/templates/T1", Ct); // case-insensitive id
        var template = detail.GetProperty("template");
        Assert.Equal("t1", template.GetProperty("id").GetString());
        var versions = template.GetProperty("versions").EnumerateArray().ToList();
        var v2 = versions[0];
        Assert.Equal(2, v2.GetProperty("number").GetInt32());
        Assert.Equal(s.V1, v2.GetProperty("parentVersionId").GetGuid());
        Assert.Equal(1, v2.GetProperty("parentNumber").GetInt32());
        Assert.Equal(new string('b', 64), v2.GetProperty("sysmonConfigSha256").GetString());
        Assert.Equal(new string('c', 64), v2.GetProperty("mitmCaSha256").GetString());
        Assert.Equal(3, v2.GetProperty("taskCount").GetInt32());
        var media = v2.GetProperty("sourceMedia").EnumerateArray().ToList();
        Assert.Equal("Win11.iso", media.Single(m => m.GetProperty("id").GetGuid() == s.Media).GetProperty("name").GetString());
        var v1 = versions[1];
        Assert.Equal(JsonValueKind.Null, v1.GetProperty("parentNumber").ValueKind);
        Assert.Equal(0, v1.GetProperty("sourceMedia").GetArrayLength());
        Assert.Equal(1, v1.GetProperty("taskCount").GetInt32());
    }

    [Fact]
    public async Task Detail_marks_deleted_source_media()
    {
        var f = Factory();
        var s = await SeedAsync(f, v2AgeDays: 2);
        using var client = f.CreateClient(UserRole.Admin);

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/admin/templates/t1", Ct);
        var media = detail.GetProperty("template").GetProperty("versions")[0].GetProperty("sourceMedia").EnumerateArray().ToList();
        var gone = media.Single(m => m.GetProperty("id").GetGuid() == s.DeletedMedia);
        Assert.Equal("(deleted)", gone.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Detail_lists_jobs_newest_first_with_requester()
    {
        var f = Factory();
        var s = await SeedAsync(f, v2AgeDays: 2);
        await AddJobAsync(f, s.UserId, TemplateJobState.Failed, 60);
        await AddJobAsync(f, s.UserId, TemplateJobState.Cancelled, 30);
        await AddJobAsync(f, s.UserId, TemplateJobState.Configuring, 5);
        using var client = f.CreateClient(UserRole.Admin);

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/admin/templates/t1", Ct);
        var jobs = detail.GetProperty("jobs").EnumerateArray().ToList();
        Assert.Equal(["Configuring", "Cancelled", "Failed"], jobs.Select(j => j.GetProperty("state").GetString()));
        Assert.All(jobs, j => Assert.Equal("Builder", j.GetProperty("requestedByName").GetString()));
        Assert.Equal(jobs[0].GetProperty("id").GetGuid(), detail.GetProperty("template").GetProperty("activeJobId").GetGuid());
    }

    [Fact]
    public async Task Detail_is_404_for_unknown_and_403_for_analyst()
    {
        var f = Factory();
        await SeedAsync(f, v2AgeDays: 2);
        using var admin = f.CreateClient(UserRole.Admin);
        using var analyst = f.CreateClient(UserRole.Analyst);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/admin/templates/nope", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.GetAsync("/api/admin/templates/t1", Ct)).StatusCode);
    }

    [Fact]
    public async Task Template_without_versions_has_no_refresh_due()
    {
        var f = Factory();
        await SeedAsync(f, v2AgeDays: 2, withVersions: false);
        using var client = f.CreateClient(UserRole.Admin);

        var t = await ListedAsync(client);
        Assert.False(t.GetProperty("refreshDue").GetBoolean());
        Assert.Equal(0, t.GetProperty("tasksLast30Days").GetInt32());
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/admin/templates/t1", Ct);
        Assert.Equal(0, detail.GetProperty("template").GetProperty("versions").GetArrayLength());
    }
}

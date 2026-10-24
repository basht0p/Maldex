using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Maldex.Integration.Tests.Web;

[Collection("sql")]
public sealed class TemplateAdminTests(SqlServerFixture sql) : IDisposable
{
    private const string Seeded = "win10-ltsc-2021"; // imported at startup: default, Ready v1

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }

    private sealed class MissingStemsStore : ITemplateStore
    {
        public HashSet<string> Missing { get; } = [];
        public bool Exists(string fileStem) => !Missing.Contains(fileStem);
        public long? SizeOf(string fileStem) => null;
        public Task DeleteAsync(string fileStem, CancellationToken ct) => Task.CompletedTask;
    }

    private MaldexAppFactory Factory()
    {
        var f = new MaldexAppFactory(sql);
        _disposables.Add(f);
        return f;
    }

    /// <summary>Adds template "other" (not default) with Ready v1 and v2, v2 current, in the circular-FK order.</summary>
    private static async Task SeedOtherAsync(MaldexAppFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var now = DateTimeOffset.UtcNow;
        var t = new Template { Id = "other", DisplayName = "Other", CreatedAt = now };
        db.Templates.Add(t);
        await db.SaveChangesAsync(Ct);
        var v1 = new TemplateVersion { Id = Guid.NewGuid(), TemplateId = "other", Number = 1, Origin = TemplateVersionOrigin.Imported, FileStem = "other-1", SealedAt = now, CurrentUntil = now.AddHours(-1) };
        var v2 = new TemplateVersion { Id = Guid.NewGuid(), TemplateId = "other", Number = 2, Origin = TemplateVersionOrigin.Imported, FileStem = "other-2", SealedAt = now };
        db.TemplateVersions.AddRange(v1, v2);
        await db.SaveChangesAsync(Ct);
        t.CurrentVersionId = v2.Id;
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<AuditLog> AuditAsync(MaldexAppFactory f, string action, string target)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        return await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == action && a.Target == target, Ct);
    }

    [Fact]
    public async Task List_ReturnsTemplatesWithVersionsNewestFirst()
    {
        var f = Factory();
        await SeedOtherAsync(f);
        using var client = f.CreateClient(UserRole.Admin);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/admin/templates", Ct);

        var other = list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == "other");
        Assert.False(other.GetProperty("isDefault").GetBoolean());
        Assert.True(other.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, other.GetProperty("current").GetProperty("number").GetInt32());
        var versions = other.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal([2, 1], versions.Select(v => v.GetProperty("number").GetInt32()));
        Assert.True(versions[0].GetProperty("isCurrent").GetBoolean());
        Assert.False(versions[1].GetProperty("isCurrent").GetBoolean());
        Assert.Equal("Ready", versions[0].GetProperty("status").GetString());
        Assert.Equal("Imported", versions[0].GetProperty("origin").GetString());
        var seeded = list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == Seeded);
        Assert.True(seeded.GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task List_ReportsRoles()
    {
        var f = Factory();
        await SeedOtherAsync(f); // v1 was current until an hour ago, v2 is current
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            var now = DateTimeOffset.UtcNow;
            TemplateVersion Extra(int n, TemplateVersionStatus status, DateTimeOffset? until) => new()
            {
                Id = Guid.NewGuid(), TemplateId = "other", Number = n, Origin = TemplateVersionOrigin.Imported, FileStem = $"other-{n}",
                SealedAt = now, Status = status, CurrentUntil = until,
            };
            db.TemplateVersions.AddRange(
                Extra(3, TemplateVersionStatus.Ready, null),                  // sealed, never current
                Extra(4, TemplateVersionStatus.Retired, now.AddHours(-5)),
                Extra(5, TemplateVersionStatus.Deleted, now.AddHours(-6)),
                Extra(6, TemplateVersionStatus.Ready, now.AddHours(-2)));     // current before v1: a further kept version
            await db.SaveChangesAsync(Ct);
        }
        using var client = f.CreateClient(UserRole.Admin);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/admin/templates", Ct);

        var roles = list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == "other").GetProperty("versions").EnumerateArray()
            .ToDictionary(v => v.GetProperty("number").GetInt32(), v => v.GetProperty("role").GetString());
        Assert.Equal("Current", roles[2]);
        Assert.Equal("RollbackTarget", roles[1]);
        Assert.Equal("Previous", roles[6]);
        Assert.Equal("Pending", roles[3]);
        Assert.Equal("Retired", roles[4]);
        Assert.Equal("Deleted", roles[5]);
    }

    [Fact]
    public async Task List_IncludesDeletedVersionsAndSealedByName()
    {
        var f = Factory();
        await SeedOtherAsync(f);
        using var client = f.CreateClient(UserRole.Admin, "sealer");
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me", Ct);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            var v1 = await db.TemplateVersions.SingleAsync(v => v.TemplateId == "other" && v.Number == 1, Ct);
            v1.Status = TemplateVersionStatus.Deleted;
            v1.SealedByUserId = me.GetProperty("id").GetGuid();
            await db.SaveChangesAsync(Ct);
        }

        var list = await client.GetFromJsonAsync<JsonElement>("/api/admin/templates", Ct);

        var v = list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == "other").GetProperty("versions").EnumerateArray().Last();
        Assert.Equal("Deleted", v.GetProperty("status").GetString());
        Assert.Equal("sealer", v.GetProperty("sealedBy").GetString());
    }

    [Fact]
    public async Task List_ShowsDiskPresent()
    {
        var store = new MissingStemsStore();
        var f = Factory().WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<ITemplateStore>(store))));
        _disposables.Add(f);
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Role", nameof(UserRole.Admin));
        var before = await client.GetFromJsonAsync<JsonElement>("/api/admin/templates", Ct); // startup import needs the files present
        Assert.True(before.EnumerateArray().Single(t => t.GetProperty("id").GetString() == Seeded).GetProperty("current").GetProperty("diskPresent").GetBoolean());
        store.Missing.Add(Seeded);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/admin/templates", Ct);

        var current = list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == Seeded).GetProperty("current");
        Assert.False(current.GetProperty("diskPresent").GetBoolean());
    }

    private sealed class InvalidStemStore : ITemplateStore
    {
        public HashSet<string> Invalid { get; } = [];
        public bool Exists(string fileStem) => Invalid.Contains(fileStem) ? throw new ArgumentException("Invalid template file stem.", nameof(fileStem)) : true;
        public long? SizeOf(string fileStem) => null;
        public Task DeleteAsync(string fileStem, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task List_InvalidStem_ShowsDiskAbsent_AndPublicListOmitsIt()
    {
        var store = new InvalidStemStore();
        var f = Factory().WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<ITemplateStore>(store))));
        _disposables.Add(f);
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Role", nameof(UserRole.Admin));
        await client.GetFromJsonAsync<JsonElement>("/api/admin/templates", Ct); // startup import needs the files present
        store.Invalid.Add(Seeded);

        var resp = await client.GetAsync("/api/admin/templates", Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.False(list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == Seeded).GetProperty("current").GetProperty("diskPresent").GetBoolean());
        var open = await client.GetAsync("/api/templates", Ct);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.DoesNotContain(Seeded, await open.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task SetDefault_Returns204_AndAudits()
    {
        var f = Factory();
        await SeedOtherAsync(f);
        using var client = f.CreateClient(UserRole.Admin);

        var resp = await client.PostAsync("/api/admin/templates/other/default", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/admin/templates", Ct);
        Assert.True(list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == "other").GetProperty("isDefault").GetBoolean());
        var audit = await AuditAsync(f, "template.default", "other");
        Assert.NotNull(audit.UserId);
    }

    [Fact]
    public async Task Disable_ThenEnable_Returns204_AndAudits()
    {
        var f = Factory();
        await SeedOtherAsync(f);
        using var client = f.CreateClient(UserRole.Admin);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/admin/templates/other/enabled", new { enabled = false }, Ct)).StatusCode);
        await AuditAsync(f, "template.disable", "other");
        Assert.DoesNotContain((await client.GetFromJsonAsync<JsonElement>("/api/templates", Ct)).EnumerateArray(), t => t.GetProperty("id").GetString() == "other");

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/admin/templates/other/enabled", new { enabled = true }, Ct)).StatusCode);
        await AuditAsync(f, "template.enable", "other");
    }

    [Fact]
    public async Task Disable_Default_Returns409()
    {
        var f = Factory();
        using var client = f.CreateClient(UserRole.Admin);

        var resp = await client.PostAsJsonAsync($"/api/admin/templates/{Seeded}/enabled", new { enabled = false }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("choose another default first", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Rollback_ReturnsTemplate_AndAuditsFromTo()
    {
        var f = Factory();
        await SeedOtherAsync(f);
        using var client = f.CreateClient(UserRole.Admin);

        var resp = await client.PostAsync("/api/admin/templates/other/rollback", null, Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("other", dto.GetProperty("id").GetString());
        Assert.Equal(1, dto.GetProperty("current").GetProperty("number").GetInt32());
        var audit = await AuditAsync(f, "template.rollback", "other");
        Assert.Equal("from=v2; to=v1", audit.Details);
    }

    [Fact]
    public async Task Rollback_NoPrevious_Returns409()
    {
        var f = Factory();
        using var client = f.CreateClient(UserRole.Admin);

        var resp = await client.PostAsync($"/api/admin/templates/{Seeded}/rollback", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Contains("no previous version", problem.GetProperty("title").GetString());
    }

    private static async Task<List<AuditLog>> TemplateAuditsAsync(MaldexAppFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        return await db.AuditLogs.AsNoTracking().Where(a => a.Action.StartsWith("template.")).ToListAsync(Ct);
    }

    [Fact]
    public async Task RefusedChange_Returns409_AndWritesNothing()
    {
        var f = Factory();
        using var client = f.CreateClient(UserRole.Admin);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/admin/templates/{Seeded}/enabled", new { enabled = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/admin/templates/{Seeded}/rollback", null, Ct)).StatusCode);

        Assert.Empty(await TemplateAuditsAsync(f));
        var seeded = (await client.GetFromJsonAsync<JsonElement>("/api/admin/templates", Ct)).EnumerateArray().Single(t => t.GetProperty("id").GetString() == Seeded);
        Assert.True(seeded.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, seeded.GetProperty("current").GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task NoOpChange_Returns204_AndWritesNoAudit()
    {
        var f = Factory();
        using var client = f.CreateClient(UserRole.Admin);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/admin/templates/{Seeded}/default", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/admin/templates/{Seeded}/enabled", new { enabled = true }, Ct)).StatusCode);

        Assert.Empty(await TemplateAuditsAsync(f));
    }

    /// <summary>Answers normally until <see cref="Fail"/> is set, then throws an InvalidOperationException that is no rule refusal.</summary>
    private sealed class FailingAfterArmStore : ITemplateStore
    {
        public volatile bool Fail;
        public bool Exists(string fileStem) => Fail ? throw new InvalidOperationException("store broke") : true;
        public long? SizeOf(string fileStem) => null;
        public Task DeleteAsync(string fileStem, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Rollback_UnexpectedInvalidOperationAfterCommit_IsNotA409()
    {
        var store = new FailingAfterArmStore();
        var baseFactory = Factory();
        await SeedOtherAsync(baseFactory);
        var f = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<ITemplateStore>(store))));
        _disposables.Add(f);
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Role", nameof(UserRole.Admin));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/templates", Ct)).StatusCode); // host started, user resolved
        store.Fail = true; // the rollback commits, then reading the result back fails

        HttpStatusCode? status = null;
        try
        {
            status = (await client.PostAsync("/api/admin/templates/other/rollback", null, Ct)).StatusCode;
        }
        catch (InvalidOperationException e) when (e.Message == "store broke")
        {
            // The test server rethrows an unhandled exception instead of answering 500; either way it is not a 409.
        }

        Assert.NotEqual(HttpStatusCode.Conflict, status);
        Assert.True(status is null or HttpStatusCode.InternalServerError, $"unexpected status {status}");
        var audit = await AuditAsync(baseFactory, "template.rollback", "other"); // committed with its audit row
        Assert.Equal("from=v2; to=v1", audit.Details);
    }

    [Fact]
    public async Task UnknownTemplate_Returns404()
    {
        var f = Factory();
        using var client = f.CreateClient(UserRole.Admin);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/admin/templates/nope/default", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/admin/templates/nope/enabled", new { enabled = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/admin/templates/nope/rollback", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task AdminRoutes_RequireAdmin()
    {
        var f = Factory();
        using var analyst = f.CreateClient(UserRole.Analyst);

        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.GetAsync("/api/admin/templates", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsync($"/api/admin/templates/{Seeded}/default", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsJsonAsync($"/api/admin/templates/{Seeded}/enabled", new { enabled = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsync($"/api/admin/templates/{Seeded}/rollback", null, Ct)).StatusCode);
    }
}

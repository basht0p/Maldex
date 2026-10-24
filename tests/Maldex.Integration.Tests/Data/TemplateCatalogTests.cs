using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Data.Templates;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;
using OrgEntity = Maldex.Core.Domain.Org;

namespace Maldex.Integration.Tests.Data;

[Collection("sql")]
public class TemplateCatalogTests(SqlServerFixture sql)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingStore : ITemplateStore
    {
        public List<string> Deleted { get; } = [];
        public bool Exists(string fileStem) => true;
        public long? SizeOf(string fileStem) => null;
        public Task DeleteAsync(string fileStem, CancellationToken ct) { Deleted.Add(fileStem); return Task.CompletedTask; }
    }

    private async Task<(MaldexDbContext db, string cs)> FreshAsync()
    {
        var cs = sql.CreateDatabaseConnectionString();
        var db = sql.CreateContext(cs);
        await db.Database.MigrateAsync(Ct);
        return (db, cs);
    }

    private static TemplateCatalog Catalog(MaldexDbContext db, int keepPrevious = 1) =>
        new(db, Options.Create(new TemplateOptions { KeepPrevious = keepPrevious }), new FixedTime(Now));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task<Template> AddTemplateAsync(MaldexDbContext db, string id, bool isDefault = false, bool enabled = true, string? name = null)
    {
        var t = new Template { Id = id, DisplayName = name ?? "Template " + id, IsDefault = isDefault, Enabled = enabled, CreatedAt = Now };
        db.Templates.Add(t);
        await db.SaveChangesAsync(Ct);
        return t;
    }

    private static async Task<TemplateVersion> AddVersionAsync(
        MaldexDbContext db, string templateId, int number, TemplateVersionStatus status = TemplateVersionStatus.Ready, bool current = false)
    {
        var v = new TemplateVersion
        {
            Id = Guid.NewGuid(), TemplateId = templateId, Number = number, Origin = TemplateVersionOrigin.IsoBuild,
            FileStem = Guid.NewGuid().ToString("N"), SealedAt = Now, Status = status,
        };
        db.TemplateVersions.Add(v);
        await db.SaveChangesAsync(Ct);
        if (current)
        {
            (await db.Templates.SingleAsync(t => t.Id == templateId, Ct)).CurrentVersionId = v.Id;
            await db.SaveChangesAsync(Ct);
        }
        return v;
    }

    private static async Task<Dictionary<int, TemplateVersionStatus>> StatusesAsync(MaldexDbContext db, string templateId)
    {
        await using var fresh = new MaldexDbContext(new DbContextOptionsBuilder<MaldexDbContext>().UseSqlServer(db.Database.GetConnectionString()).Options);
        return await fresh.TemplateVersions.Where(v => v.TemplateId == templateId).ToDictionaryAsync(v => v.Number, v => v.Status, Ct);
    }

    private static async Task<Guid?> CurrentAsync(MaldexDbContext db, string templateId)
    {
        await using var fresh = new MaldexDbContext(new DbContextOptionsBuilder<MaldexDbContext>().UseSqlServer(db.Database.GetConnectionString()).Options);
        return (await fresh.Templates.SingleAsync(t => t.Id == templateId, Ct)).CurrentVersionId;
    }

    // --- lookup ---

    [Fact]
    public async Task CurrentReadyVersion_DisabledTemplate_Throws()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a", enabled: false);
            await AddVersionAsync(db, "a", 1, current: true);
            await Assert.ThrowsAsync<TemplateUnavailableException>(() => Catalog(db).CurrentReadyVersionAsync("a", Ct));
        }
    }

    [Fact]
    public async Task CurrentReadyVersion_NoCurrent_Throws()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            await AddVersionAsync(db, "a", 1);
            await Assert.ThrowsAsync<TemplateUnavailableException>(() => Catalog(db).CurrentReadyVersionAsync("a", Ct));
            await Assert.ThrowsAsync<TemplateUnavailableException>(() => Catalog(db).CurrentReadyVersionAsync("missing", Ct));
        }
    }

    [Fact]
    public async Task CurrentReadyVersion_CurrentRetired_Throws()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            await AddVersionAsync(db, "a", 1, TemplateVersionStatus.Retired, current: true);
            await Assert.ThrowsAsync<TemplateUnavailableException>(() => Catalog(db).CurrentReadyVersionAsync("a", Ct));
        }
    }

    [Fact]
    public async Task CurrentReadyVersion_Ready_ReturnsVersion()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            var v = await AddVersionAsync(db, "a", 1, current: true);
            Assert.Equal(v.Id, (await Catalog(db).CurrentReadyVersionAsync("A", Ct)).Id);
        }
    }

    [Fact]
    public async Task FindEnabled_IsCaseInsensitive_ReturnsCanonicalId()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "win11-26h2");
            await AddTemplateAsync(db, "off", enabled: false);
            var found = await Catalog(db).FindEnabledAsync("WIN11-26H2", Ct);
            Assert.Equal("win11-26h2", found?.Id);
            Assert.Null(await Catalog(db).FindEnabledAsync("off", Ct));
            Assert.Null(await Catalog(db).FindEnabledAsync("nope", Ct));
        }
    }

    [Fact]
    public async Task Enabled_OrdersDefaultFirstThenDisplayName_AndDefaultAsync()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "b", name: "Bravo");
            await AddTemplateAsync(db, "a", name: "Alpha");
            await AddTemplateAsync(db, "z", isDefault: true, name: "Zulu");
            await AddTemplateAsync(db, "off", enabled: false, name: "Aaa");
            var catalog = Catalog(db);
            Assert.Equal(["z", "a", "b"], (await catalog.EnabledAsync(Ct)).Select(t => t.Id));
            Assert.Equal("z", (await catalog.DefaultAsync(Ct))?.Id);
        }
    }

    // --- publish / rollback ---

    [Fact]
    public async Task Publish_KeepsCurrentPlusOnePrevious_RetiresOlder()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            await AddVersionAsync(db, "a", 1);
            await AddVersionAsync(db, "a", 2);
            await AddVersionAsync(db, "a", 3);
            var v4 = await AddVersionAsync(db, "a", 4);

            await Catalog(db).PublishAsync("a", v4.Id, Ct);

            Assert.Equal(v4.Id, await CurrentAsync(db, "a"));
            var s = await StatusesAsync(db, "a");
            Assert.Equal(TemplateVersionStatus.Ready, s[4]);
            Assert.Equal(TemplateVersionStatus.Ready, s[3]);
            Assert.Equal(TemplateVersionStatus.Retired, s[2]);
            Assert.Equal(TemplateVersionStatus.Retired, s[1]);
        }
    }

    [Fact]
    public async Task Publish_KeepPreviousZero_RetiresAllOthers()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            await AddVersionAsync(db, "a", 1);
            await AddVersionAsync(db, "a", 2);
            var v3 = await AddVersionAsync(db, "a", 3);

            await Catalog(db, keepPrevious: 0).PublishAsync("a", v3.Id, Ct);

            var s = await StatusesAsync(db, "a");
            Assert.Equal(TemplateVersionStatus.Ready, s[3]);
            Assert.Equal(TemplateVersionStatus.Retired, s[2]);
            Assert.Equal(TemplateVersionStatus.Retired, s[1]);
        }
    }

    [Fact]
    public async Task Publish_VersionOfAnotherTemplate_OrNotReady_Throws()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            await AddTemplateAsync(db, "b");
            var other = await AddVersionAsync(db, "b", 1);
            var retired = await AddVersionAsync(db, "a", 1, TemplateVersionStatus.Retired);
            var catalog = Catalog(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.PublishAsync("a", other.Id, Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.PublishAsync("a", retired.Id, Ct));
            Assert.Null(await CurrentAsync(db, "a"));
        }
    }

    [Fact]
    public async Task RollBack_PublishesPreviousVersion()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            await AddVersionAsync(db, "a", 1, TemplateVersionStatus.Retired);
            var v2 = await AddVersionAsync(db, "a", 2);
            await AddVersionAsync(db, "a", 3, current: true);

            await Catalog(db).RollBackAsync("a", Ct);

            Assert.Equal(v2.Id, await CurrentAsync(db, "a"));
            var s = await StatusesAsync(db, "a");
            Assert.Equal(TemplateVersionStatus.Ready, s[2]);
            Assert.Equal(TemplateVersionStatus.Ready, s[3]);
            Assert.Equal(TemplateVersionStatus.Retired, s[1]);
        }
    }

    [Fact]
    public async Task RollBack_WithNoPrevious_Throws()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            await AddVersionAsync(db, "a", 1, TemplateVersionStatus.Retired);
            var v2 = await AddVersionAsync(db, "a", 2, current: true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog(db).RollBackAsync("a", Ct));
            Assert.Equal(v2.Id, await CurrentAsync(db, "a"));
        }
    }

    // --- default / enabled ---

    [Fact]
    public async Task SetDefault_MovesDefault()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a", isDefault: true);
            await AddTemplateAsync(db, "b");
            await Catalog(db).SetDefaultAsync("b", Ct);

            await using var fresh = sql.CreateContext(db.Database.GetConnectionString()!);
            Assert.Equal(["b"], await fresh.Templates.Where(t => t.IsDefault).Select(t => t.Id).ToListAsync(Ct));
        }
    }

    [Fact]
    public async Task SetDefault_DisabledTemplate_Throws()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a", isDefault: true);
            await AddTemplateAsync(db, "b", enabled: false);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog(db).SetDefaultAsync("b", Ct));
            Assert.Equal("a", (await Catalog(db).DefaultAsync(Ct))?.Id);
        }
    }

    [Fact]
    public async Task SetEnabled_False_OnDefault_Throws()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a", isDefault: true);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog(db).SetEnabledAsync("a", false, Ct));
            Assert.Equal("choose another default first", ex.Message);
        }
    }

    [Fact]
    public async Task SetEnabled_NonDefault_Toggles()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a", isDefault: true);
            await AddTemplateAsync(db, "b");
            await Catalog(db).SetEnabledAsync("b", false, Ct);
            Assert.Null(await Catalog(db).FindEnabledAsync("b", Ct));
            await Catalog(db).SetEnabledAsync("b", true, Ct);
            Assert.NotNull(await Catalog(db).FindEnabledAsync("b", Ct));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetDefault_Concurrent_LeavesExactlyOneDefault(bool withOldDefault)
    {
        var (db, cs) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a", isDefault: withOldDefault);
            await AddTemplateAsync(db, "b");
            await AddTemplateAsync(db, "c");

            await using var db1 = sql.CreateContext(cs);
            await using var db2 = sql.CreateContext(cs);
            await Task.WhenAll(
                Task.Run(() => Catalog(db1).SetDefaultAsync("b", Ct), Ct),
                Task.Run(() => Catalog(db2).SetDefaultAsync("c", Ct), Ct));

            await using var check = sql.CreateContext(cs);
            Assert.Single(await check.Templates.Where(t => t.IsDefault).ToListAsync(Ct));
        }
    }

    // --- sweep ---

    private static async Task SeedTaskAsync(MaldexDbContext db, Guid versionId, RunStatus status)
    {
        var org = new OrgEntity { Id = Guid.NewGuid(), Name = "Org-" + Guid.NewGuid().ToString("N"), RetentionDays = 30 };
        var user = new AppUser { Id = Guid.NewGuid(), EntraObjectId = Guid.NewGuid().ToString("N"), DisplayName = "A", Role = UserRole.Analyst };
        var task = new AnalysisTask
        {
            Id = Guid.NewGuid(), OrgId = org.Id, SubmittedByUserId = user.Id, Url = "http://x.test",
            Image = "a", NetworkMode = NetworkMode.Simulated, DurationSec = 300, CreatedAt = Now, TemplateVersionId = versionId,
        };
        // Walk the legal state machine to the wanted status.
        RunStatus[] path = status switch
        {
            RunStatus.Preparing => [RunStatus.Preparing],
            RunStatus.Running => [RunStatus.Preparing, RunStatus.Running],
            RunStatus.Completed => [RunStatus.Preparing, RunStatus.Running, RunStatus.Collecting, RunStatus.Processing, RunStatus.Completed],
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        foreach (var s in path) task.TransitionTo(s, "test", Now);
        db.AddRange(org, user, task);
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Sweep_DeletesRetiredUnused_MarksDeleted()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            var retired = await AddVersionAsync(db, "a", 1, TemplateVersionStatus.Retired);
            var store = new RecordingStore();

            var deleted = await Catalog(db).SweepAsync(store, Ct);

            Assert.Equal([retired.Id], deleted.Select(v => v.Id));
            Assert.Equal([retired.FileStem], store.Deleted);
            Assert.Equal(TemplateVersionStatus.Deleted, (await StatusesAsync(db, "a"))[1]);
            // A second sweep has nothing left to do.
            Assert.Empty(await Catalog(db).SweepAsync(store, Ct));
            Assert.Single(store.Deleted);
        }
    }

    [Theory]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Preparing)]
    public async Task Sweep_KeepsRetiredVersion_UsedByActiveTask(RunStatus status)
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            var retired = await AddVersionAsync(db, "a", 1, TemplateVersionStatus.Retired);
            await SeedTaskAsync(db, retired.Id, status);
            var store = new RecordingStore();

            Assert.Empty(await Catalog(db).SweepAsync(store, Ct));

            Assert.Empty(store.Deleted);
            Assert.Equal(TemplateVersionStatus.Retired, (await StatusesAsync(db, "a"))[1]);
        }
    }

    [Fact]
    public async Task Sweep_CompletedTask_DoesNotKeepRetiredVersion()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            var retired = await AddVersionAsync(db, "a", 1, TemplateVersionStatus.Retired);
            await SeedTaskAsync(db, retired.Id, RunStatus.Completed);
            var store = new RecordingStore();

            Assert.Single(await Catalog(db).SweepAsync(store, Ct));

            Assert.Equal([retired.FileStem], store.Deleted);
            Assert.Equal(TemplateVersionStatus.Deleted, (await StatusesAsync(db, "a"))[1]);
        }
    }

    [Fact]
    public async Task Sweep_NeverDeletesReady()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            await AddVersionAsync(db, "a", 1);
            await AddVersionAsync(db, "a", 2, current: true);
            var store = new RecordingStore();

            Assert.Empty(await Catalog(db).SweepAsync(store, Ct));

            Assert.Empty(store.Deleted);
            Assert.All(await StatusesAsync(db, "a"), kv => Assert.Equal(TemplateVersionStatus.Ready, kv.Value));
        }
    }
}

using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Data.Templates;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
        public Task CreateBlankAsync(string stem, long diskBytes, string varsTemplatePath, CancellationToken ct) => throw new NotSupportedException();
        public List<string> Deleted { get; } = [];
        public HashSet<string> Missing { get; } = [];
        public bool Exists(string fileStem) => !Missing.Contains(fileStem);
        public long? SizeOf(string fileStem) => null;
        public HashSet<string> FailStems { get; } = [];
        public Task DeleteAsync(string fileStem, CancellationToken ct)
        {
            if (FailStems.Contains(fileStem)) throw new IOException("disk busy");
            Deleted.Add(fileStem);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingSaveInterceptor(Func<int, Exception?> failOnCall) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public int Calls { get; private set; }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var ex = failOnCall(++Calls);
            if (ex is not null) throw ex;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private async Task<(MaldexDbContext db, string cs)> FreshAsync()
    {
        var cs = sql.CreateDatabaseConnectionString();
        var db = sql.CreateContext(cs);
        await db.Database.MigrateAsync(Ct);
        return (db, cs);
    }

    private static TemplateCatalog Catalog(MaldexDbContext db, int keepPrevious = 1, ITemplateStore? store = null, DateTimeOffset? now = null) =>
        new(db, store ?? new RecordingStore(), Options.Create(new TemplateOptions { KeepPrevious = keepPrevious }), new FixedTime(now ?? Now));

    /// <summary>A moment before <see cref="Now"/>, <paramref name="hoursAgo"/> hours back: larger values are earlier.</summary>
    private static DateTimeOffset Ago(int hoursAgo) => Now.AddHours(-hoursAgo);

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
        MaldexDbContext db, string templateId, int number, TemplateVersionStatus status = TemplateVersionStatus.Ready, bool current = false,
        DateTimeOffset? currentUntil = null)
    {
        var v = new TemplateVersion
        {
            Id = Guid.NewGuid(), TemplateId = templateId, Number = number, Origin = TemplateVersionOrigin.IsoBuild,
            FileStem = Guid.NewGuid().ToString("N"), SealedAt = Now, Status = status, CurrentUntil = currentUntil,
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

    /// <summary>Like <c>LibvirtTemplateStore</c>: a stem that is not a valid slug throws instead of answering.</summary>
    private sealed class InvalidStemStore(string invalid) : ITemplateStore
    {
        public Task CreateBlankAsync(string stem, long diskBytes, string varsTemplatePath, CancellationToken ct) => throw new NotSupportedException();
        public bool Exists(string fileStem) => fileStem == invalid ? throw new ArgumentException("Invalid template file stem.", nameof(fileStem)) : true;
        public long? SizeOf(string fileStem) => fileStem == invalid ? throw new ArgumentException("Invalid template file stem.", nameof(fileStem)) : null;
        public Task DeleteAsync(string fileStem, CancellationToken ct) => Task.CompletedTask;
    }

    private static async Task<TemplateVersion> AddInvalidStemTemplateAsync(MaldexDbContext db)
    {
        await AddTemplateAsync(db, "good", isDefault: true);
        await AddVersionAsync(db, "good", 1, current: true);
        await AddTemplateAsync(db, "weird");
        var v = await AddVersionAsync(db, "weird", 1, current: true);
        v.FileStem = "Not_A Stem";
        await db.SaveChangesAsync(Ct);
        return v;
    }

    [Fact]
    public async Task InvalidStem_IsUnavailable_LikeAMissingDisk()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            var v = await AddInvalidStemTemplateAsync(db);
            var catalog = Catalog(db, store: new InvalidStemStore(v.FileStem));

            Assert.Equal(["good"], (await catalog.AvailableAsync(Ct)).Select(t => t.Id));
            Assert.Null(await catalog.FindAvailableAsync("weird", Ct));
            var ex = await Assert.ThrowsAsync<TemplateUnavailableException>(() => catalog.CurrentReadyVersionAsync("weird", Ct));
            Assert.Equal("template 'weird' is not available: its disk is missing", ex.Message);
        }
    }

    [Fact]
    public async Task InvalidStem_AlreadyBoundTask_RefusesWithDiskMissing()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            var v = await AddInvalidStemTemplateAsync(db);
            var task = new AnalysisTask { TemplateVersionId = v.Id, Image = "weird" };

            var ex = await Assert.ThrowsAsync<TemplateUnavailableException>(
                () => Catalog(db, store: new InvalidStemStore(v.FileStem)).BindTaskVersionAsync(task, Ct));

            Assert.Equal("template 'weird' is not available: its disk is missing", ex.Message);
        }
    }

    [Fact]
    public async Task FindEnabled_IsCaseInsensitive_ReturnsCanonicalId()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "win11-26h2");
            await AddVersionAsync(db, "win11-26h2", 1, current: true);
            await AddTemplateAsync(db, "off", enabled: false);
            await AddVersionAsync(db, "off", 1, current: true);
            var found = await Catalog(db).FindAvailableAsync("WIN11-26H2", Ct);
            Assert.Equal("win11-26h2", found?.Id);
            Assert.Null(await Catalog(db).FindAvailableAsync("off", Ct));
            Assert.Null(await Catalog(db).FindAvailableAsync("nope", Ct));
        }
    }

    [Fact]
    public async Task Enabled_OrdersDefaultFirstThenDisplayName_AndDefaultAsync()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            foreach (var (id, name, isDefault, enabled) in new[] { ("b", "Bravo", false, true), ("a", "Alpha", false, true), ("z", "Zulu", true, true), ("off", "Aaa", false, false) })
            {
                await AddTemplateAsync(db, id, isDefault, enabled, name);
                await AddVersionAsync(db, id, 1, current: true);
            }
            var catalog = Catalog(db);
            Assert.Equal(["z", "a", "b"], (await catalog.AvailableAsync(Ct)).Select(t => t.Id));
            Assert.Equal("z", (await catalog.DefaultAsync(Ct))?.Id);
        }
    }

    [Fact]
    public async Task Available_RequiresEnabledReadyCurrentVersionWithItsDisk()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "ok", name: "A ok");
            await AddVersionAsync(db, "ok", 1, current: true);
            await AddTemplateAsync(db, "no-current", name: "B no current");
            await AddVersionAsync(db, "no-current", 1);
            await AddTemplateAsync(db, "retired", name: "C retired");
            await AddVersionAsync(db, "retired", 1, TemplateVersionStatus.Retired, current: true);
            await AddTemplateAsync(db, "no-disk", isDefault: true, name: "D no disk");
            var noDisk = await AddVersionAsync(db, "no-disk", 1, current: true);
            await AddVersionAsync(db, "no-disk", 2); // another version's disk does not count
            var store = new RecordingStore { Missing = { noDisk.FileStem } };
            var catalog = Catalog(db, store: store);

            Assert.Equal(["ok"], (await catalog.AvailableAsync(Ct)).Select(t => t.Id));
            Assert.Equal("ok", (await catalog.FindAvailableAsync("ok", Ct))?.Id);
            foreach (var id in new[] { "no-current", "retired", "no-disk" })
                Assert.Null(await catalog.FindAvailableAsync(id, Ct));
            Assert.Null(await catalog.DefaultAsync(Ct)); // the default is not usable without its disk
        }
    }

    [Fact]
    public async Task CurrentReadyVersion_DiskMissing_Throws()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            var v = await AddVersionAsync(db, "a", 1, current: true);
            var store = new RecordingStore { Missing = { v.FileStem } };
            var ex = await Assert.ThrowsAsync<TemplateUnavailableException>(() => Catalog(db, store: store).CurrentReadyVersionAsync("a", Ct));
            Assert.Equal("template 'a' is not available: its disk is missing", ex.Message);
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
            await AddVersionAsync(db, "a", 1, currentUntil: Ago(3));
            await AddVersionAsync(db, "a", 2, currentUntil: Ago(2));
            await AddVersionAsync(db, "a", 3, currentUntil: Ago(1));
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
            await AddVersionAsync(db, "a", 1, currentUntil: Ago(2));
            await AddVersionAsync(db, "a", 2, currentUntil: Ago(1));
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
            await Assert.ThrowsAsync<TemplateRuleException>(() => catalog.PublishAsync("a", other.Id, Ct));
            await Assert.ThrowsAsync<TemplateRuleException>(() => catalog.PublishAsync("a", retired.Id, Ct));
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
            var v2 = await AddVersionAsync(db, "a", 2, currentUntil: Ago(1));
            await AddVersionAsync(db, "a", 3, current: true);

            await Catalog(db).RollBackAsync("a", Ct);

            Assert.Equal(v2.Id, await CurrentAsync(db, "a"));
            var s = await StatusesAsync(db, "a");
            Assert.Equal(TemplateVersionStatus.Ready, s[2]);
            Assert.Equal(TemplateVersionStatus.Retired, s[3]); // the version rolled back from is never kept
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
            await Assert.ThrowsAsync<TemplateRuleException>(() => Catalog(db).RollBackAsync("a", Ct));
            Assert.Equal(v2.Id, await CurrentAsync(db, "a"));
        }
    }


    private static async Task<Dictionary<int, DateTimeOffset?>> CurrentUntilAsync(MaldexDbContext db, string templateId)
    {
        await using var fresh = new MaldexDbContext(new DbContextOptionsBuilder<MaldexDbContext>().UseSqlServer(db.Database.GetConnectionString()).Options);
        return await fresh.TemplateVersions.Where(v => v.TemplateId == templateId).ToDictionaryAsync(v => v.Number, v => v.CurrentUntil, Ct);
    }

    // --- retention by "was current before" (basht0p, 2026-10-24) ---

    [Fact]
    public async Task Publish_OldCurrentBecomesPrevious()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            await AddVersionAsync(db, "a", 1, current: true);
            var v2 = await AddVersionAsync(db, "a", 2);

            await Catalog(db).PublishAsync("a", v2.Id, Ct);

            Assert.Equal(v2.Id, await CurrentAsync(db, "a"));
            var until = await CurrentUntilAsync(db, "a");
            Assert.Equal(Now, until[1]);
            Assert.Null(until[2]);
            Assert.Equal(TemplateVersionStatus.Ready, (await StatusesAsync(db, "a"))[1]);
        }
    }

    [Fact]
    public async Task RollBack_RetiresTheVersionRolledBackFrom()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            var v1 = await AddVersionAsync(db, "a", 1, currentUntil: Ago(1));
            await AddVersionAsync(db, "a", 2, current: true);

            var result = await Catalog(db).RollBackAsync("a", Ct);

            Assert.Equal(new RollBackResult(2, 1), result);
            Assert.Equal(v1.Id, await CurrentAsync(db, "a"));
            Assert.Equal(TemplateVersionStatus.Retired, (await StatusesAsync(db, "a"))[2]);
            Assert.Equal(Now, (await CurrentUntilAsync(db, "a"))[2]);
        }
    }

    [Fact]
    public async Task RollBack_TargetIsTheMostRecentlyCurrent_NotTheHighestNumber()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            await AddVersionAsync(db, "a", 1, current: true);
            var v2 = await AddVersionAsync(db, "a", 2);
            var v3 = await AddVersionAsync(db, "a", 3);
            var v4 = await AddVersionAsync(db, "a", 4);
            await Catalog(db, now: Ago(30)).PublishAsync("a", v2.Id, Ct);
            await Catalog(db, now: Ago(20)).PublishAsync("a", v3.Id, Ct); // history v1 -> v2 -> v3 (KeepPrevious 1 retired v1)

            await Catalog(db, now: Ago(10)).RollBackAsync("a", Ct); // from v3: the target is v2, not a higher number

            Assert.Equal(v2.Id, await CurrentAsync(db, "a"));
            var s = await StatusesAsync(db, "a");
            Assert.Equal(TemplateVersionStatus.Retired, s[3]);
            Assert.Equal(TemplateVersionStatus.Ready, s[4]); // never current: pending, untouched

            await Catalog(db, now: Ago(5)).PublishAsync("a", v4.Id, Ct);

            s = await StatusesAsync(db, "a");
            Assert.Equal(TemplateVersionStatus.Ready, s[2]); // v2 was current before v4: it is the previous version
            Assert.Equal(TemplateVersionStatus.Retired, s[3]);
        }
    }

    [Fact]
    public async Task Publish_PendingVersionsAreNotRetired()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            await AddVersionAsync(db, "a", 1, current: true);
            var v2 = await AddVersionAsync(db, "a", 2);
            await AddVersionAsync(db, "a", 3); // sealed, never published
            await AddVersionAsync(db, "a", 4); // sealed, never published

            await Catalog(db, keepPrevious: 0).PublishAsync("a", v2.Id, Ct);

            var s = await StatusesAsync(db, "a");
            Assert.Equal(TemplateVersionStatus.Retired, s[1]); // KeepPrevious 0: the old current goes
            Assert.Equal(TemplateVersionStatus.Ready, s[3]);
            Assert.Equal(TemplateVersionStatus.Ready, s[4]);
        }
    }

    [Fact]
    public async Task Publish_PendingVersionsDoNotCountTowardKeepPrevious()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            await AddVersionAsync(db, "a", 1, current: true);
            var v2 = await AddVersionAsync(db, "a", 2);
            await AddVersionAsync(db, "a", 3);

            await Catalog(db).PublishAsync("a", v2.Id, Ct);

            var s = await StatusesAsync(db, "a");
            Assert.Equal(TemplateVersionStatus.Ready, s[1]);
            Assert.Equal(TemplateVersionStatus.Ready, s[3]);
        }
    }

    [Fact]
    public async Task RollBack_PendingVersionIsNotATarget()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            var v1 = await AddVersionAsync(db, "a", 1, current: true);
            await AddVersionAsync(db, "a", 2); // never current

            await Assert.ThrowsAsync<TemplateRuleException>(() => Catalog(db).RollBackAsync("a", Ct));
            Assert.Equal(v1.Id, await CurrentAsync(db, "a"));
        }
    }

    [Fact]
    public async Task RollBack_Twice_NeverReturnsToTheBadVersion()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            await AddVersionAsync(db, "a", 1, current: true);
            var v2 = await AddVersionAsync(db, "a", 2);
            await Catalog(db, keepPrevious: 2, now: Ago(20)).PublishAsync("a", v2.Id, Ct); // v1 previous, v2 current
            var v3 = await AddVersionAsync(db, "a", 3);
            await Catalog(db, keepPrevious: 2, now: Ago(10)).PublishAsync("a", v3.Id, Ct); // v3 current, v2 then v1 previous

            await Catalog(db, keepPrevious: 2, now: Ago(5)).RollBackAsync("a", Ct); // v3 is bad: back to v2
            Assert.Equal(v2.Id, await CurrentAsync(db, "a"));
            await Catalog(db, keepPrevious: 2, now: Ago(4)).RollBackAsync("a", Ct); // v2 is bad too: back to v1, never to v3

            var s = await StatusesAsync(db, "a");
            Assert.Equal(TemplateVersionStatus.Ready, s[1]);
            Assert.Equal(TemplateVersionStatus.Retired, s[2]);
            Assert.Equal(TemplateVersionStatus.Retired, s[3]);
            await Assert.ThrowsAsync<TemplateRuleException>(() => Catalog(db, keepPrevious: 2).RollBackAsync("a", Ct));
        }
    }

    [Fact]
    public async Task Migration_BackfillsCurrentUntilOfNonCurrentReadyVersions()
    {
        var cs = sql.CreateDatabaseConnectionString();
        await using var db = sql.CreateContext(cs);
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20261024060433_TemplateVersionFileStemUnique", cancellationToken: Ct);
        var sealedAt = Now.AddDays(-3);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Templates (Id, DisplayName, IsDefault, Enabled, CreatedAt) VALUES ('a', 'A', 0, 1, {0})", [Now], Ct);
        foreach (var (n, status) in new[] { (1, "Ready"), (2, "Retired"), (3, "Ready") })
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO TemplateVersions (Id, TemplateId, Number, Origin, FileStem, SourceMediaIds, SealedAt, Status) VALUES ({0}, 'a', {1}, 'IsoBuild', {2}, '[]', {3}, {4})",
                [Guid.NewGuid(), n, "stem" + n, sealedAt, status], Ct);
        await db.Database.ExecuteSqlRawAsync("UPDATE Templates SET CurrentVersionId = (SELECT Id FROM TemplateVersions WHERE Number = 3) WHERE Id = 'a'", [], Ct);

        await db.Database.MigrateAsync(Ct);

        var until = await CurrentUntilAsync(db, "a");
        Assert.Equal(sealedAt, until[1]); // non-current Ready: was current before
        Assert.Null(until[2]);            // Retired: untouched
        Assert.Null(until[3]);            // current
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
    public async Task SetDefault_InsideAnOpenTransaction_JoinsIt_AndCommitsWithIt()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a", isDefault: true);
            await AddTemplateAsync(db, "b");
            await using (var tx = await db.Database.BeginTransactionAsync(Ct))
            {
                await Catalog(db).SetDefaultAsync("b", Ct, new TemplateAudit(null, "template.default", "first template"));
                await tx.CommitAsync(Ct);
            }

            await using var fresh = sql.CreateContext(db.Database.GetConnectionString()!);
            Assert.Equal(["b"], await fresh.Templates.Where(t => t.IsDefault).Select(t => t.Id).ToListAsync(Ct));
            var audit = await fresh.AuditLogs.SingleAsync(Ct);
            Assert.Equal(("template.default", (Guid?)null, "b", "first template"), (audit.Action, audit.UserId, audit.Target, audit.Details));
        }
    }

    [Fact]
    public async Task SetDefault_InsideAnOpenTransaction_RollsBackWithIt()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a", isDefault: true);
            await AddTemplateAsync(db, "b");
            await using (var tx = await db.Database.BeginTransactionAsync(Ct))
            {
                await Catalog(db).SetDefaultAsync("b", Ct, new TemplateAudit(null, "template.default"));
                await tx.RollbackAsync(Ct);
            }

            await using var fresh = sql.CreateContext(db.Database.GetConnectionString()!);
            Assert.Equal(["a"], await fresh.Templates.Where(t => t.IsDefault).Select(t => t.Id).ToListAsync(Ct));
            Assert.Empty(await fresh.AuditLogs.ToListAsync(Ct));
        }
    }

    [Fact]
    public async Task SetDefault_DisabledTemplate_Throws()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a", isDefault: true);
            await AddVersionAsync(db, "a", 1, current: true);
            await AddTemplateAsync(db, "b", enabled: false);
            await Assert.ThrowsAsync<TemplateRuleException>(() => Catalog(db).SetDefaultAsync("b", Ct));
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
            var ex = await Assert.ThrowsAsync<TemplateRuleException>(() => Catalog(db).SetEnabledAsync("a", false, Ct));
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
            await AddVersionAsync(db, "b", 1, current: true);
            await Catalog(db).SetEnabledAsync("b", false, Ct);
            Assert.Null(await Catalog(db).FindAvailableAsync("b", Ct));
            await Catalog(db).SetEnabledAsync("b", true, Ct);
            Assert.NotNull(await Catalog(db).FindAvailableAsync("b", Ct));
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
            RunStatus.Collecting => [RunStatus.Preparing, RunStatus.Running, RunStatus.Collecting],
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

            var deleted = await Catalog(db, store: store).SweepAsync(Ct);

            Assert.Equal([retired.Id], deleted.Select(v => v.Id));
            Assert.Equal([retired.FileStem], store.Deleted);
            Assert.Equal(TemplateVersionStatus.Deleted, (await StatusesAsync(db, "a"))[1]);
            // A second sweep has nothing left to do.
            Assert.Empty(await Catalog(db, store: store).SweepAsync(Ct));
            Assert.Single(store.Deleted);
        }
    }

    [Theory]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Preparing)]
    [InlineData(RunStatus.Collecting)]
    public async Task Sweep_KeepsRetiredVersion_UsedByActiveTask(RunStatus status)
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            var retired = await AddVersionAsync(db, "a", 1, TemplateVersionStatus.Retired);
            await SeedTaskAsync(db, retired.Id, status);
            var store = new RecordingStore();

            Assert.Empty(await Catalog(db, store: store).SweepAsync(Ct));

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

            Assert.Single(await Catalog(db, store: store).SweepAsync(Ct));

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

            Assert.Empty(await Catalog(db, store: store).SweepAsync(Ct));

            Assert.Empty(store.Deleted);
            Assert.All(await StatusesAsync(db, "a"), kv => Assert.Equal(TemplateVersionStatus.Ready, kv.Value));
        }
    }

    [Fact]
    public async Task Sweep_FailingDelete_DoesNotStopOthers()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            var bad = await AddVersionAsync(db, "a", 1, TemplateVersionStatus.Retired);
            var good = await AddVersionAsync(db, "a", 2, TemplateVersionStatus.Retired);
            var store = new RecordingStore();
            store.FailStems.Add(bad.FileStem);

            var deleted = await Catalog(db, store: store).SweepAsync(Ct);

            Assert.Equal([good.Id], deleted.Select(v => v.Id));
            var s = await StatusesAsync(db, "a");
            Assert.Equal(TemplateVersionStatus.Retired, s[1]);
            Assert.Equal(TemplateVersionStatus.Deleted, s[2]);
        }
    }

    private static async Task SeedJobAsync(MaldexDbContext db, string templateId, Guid baseVersionId, TemplateJobState state)
    {
        db.TemplateJobs.Add(new TemplateJob
        {
            Id = Guid.NewGuid(), Kind = TemplateJobKind.Refresh, TemplateId = templateId, BaseVersionId = baseVersionId, State = state,
            RequestedByUserId = await AddUserAsync(db), CreatedAt = Now,
        });
        await db.SaveChangesAsync(Ct);
    }

    [Theory]
    [InlineData(TemplateJobState.Queued)]
    [InlineData(TemplateJobState.Preparing)]
    [InlineData(TemplateJobState.Configuring)]
    [InlineData(TemplateJobState.Sealing)]
    [InlineData(TemplateJobState.Verifying)]
    public async Task Sweep_KeepsRetiredVersion_ThatIsTheBaseOfAnActiveJob(TemplateJobState state)
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            var retired = await AddVersionAsync(db, "a", 1, TemplateVersionStatus.Retired);
            await SeedJobAsync(db, "a", retired.Id, state);
            var store = new RecordingStore();

            Assert.Empty(await Catalog(db, store: store).SweepAsync(Ct));

            Assert.Empty(store.Deleted);
            Assert.Equal(TemplateVersionStatus.Retired, (await StatusesAsync(db, "a"))[1]);
        }
    }

    [Theory]
    [InlineData(TemplateJobState.Published)]
    [InlineData(TemplateJobState.Sealed)]
    [InlineData(TemplateJobState.Failed)]
    [InlineData(TemplateJobState.Cancelled)]
    public async Task Sweep_EndedJob_DoesNotKeepItsRetiredBaseVersion(TemplateJobState state)
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            var retired = await AddVersionAsync(db, "a", 1, TemplateVersionStatus.Retired);
            await SeedJobAsync(db, "a", retired.Id, state);
            var store = new RecordingStore();

            Assert.Single(await Catalog(db, store: store).SweepAsync(Ct));

            Assert.Equal([retired.FileStem], store.Deleted);
        }
    }

    // --- audit ---

    /// <summary>Fails any save that carries an audit row, as a failing audit write would.</summary>
    private sealed class FailingAuditInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AuditLog>().Any(e => e.State == EntityState.Added))
                throw new DbUpdateException("audit write failed");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static async Task<Guid> AddUserAsync(MaldexDbContext db)
    {
        var user = new AppUser { Id = Guid.NewGuid(), EntraObjectId = Guid.NewGuid().ToString("N"), DisplayName = "Admin", Role = UserRole.Admin };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        return user.Id;
    }

    private static async Task<List<AuditLog>> AuditsAsync(MaldexDbContext db)
    {
        await using var fresh = new MaldexDbContext(new DbContextOptionsBuilder<MaldexDbContext>().UseSqlServer(db.Database.GetConnectionString()).Options);
        return await fresh.AuditLogs.AsNoTracking().OrderBy(a => a.Id).ToListAsync(Ct);
    }

    /// <summary>"a": default, Ready v1 and Ready v2 (current). "b": enabled, Ready v1 current. Returns a's v1.</summary>
    private static async Task<TemplateVersion> SeedAuditCaseAsync(MaldexDbContext db)
    {
        await AddTemplateAsync(db, "a", isDefault: true);
        var a1 = await AddVersionAsync(db, "a", 1, currentUntil: Ago(1));
        await AddVersionAsync(db, "a", 2, current: true);
        await AddTemplateAsync(db, "b");
        await AddVersionAsync(db, "b", 1, current: true);
        return a1;
    }

    /// <summary>Runs one admin change through <paramref name="catalog"/>, audited as <paramref name="userId"/>.</summary>
    private static Task ChangeAsync(TemplateCatalog catalog, string change, Guid userId, Guid aV1) => change switch
    {
        "default" => catalog.SetDefaultAsync("b", Ct, new TemplateAudit(userId, "template.default")),
        "disable" => catalog.SetEnabledAsync("b", false, Ct, new TemplateAudit(userId, "template.disable")),
        "publish" => catalog.PublishAsync("a", aV1, Ct, new TemplateAudit(userId, "template.publish", "v=1")),
        "rollback" => catalog.RollBackAsync("a", Ct, r => new TemplateAudit(userId, "template.rollback", $"from=v{r.From}; to=v{r.To}")),
        _ => throw new ArgumentOutOfRangeException(nameof(change)),
    };

    [Theory]
    [InlineData("default", "template.default", "b", null)]
    [InlineData("disable", "template.disable", "b", null)]
    [InlineData("publish", "template.publish", "a", "v=1")]
    [InlineData("rollback", "template.rollback", "a", "from=v2; to=v1")]
    public async Task Change_WritesItsAuditRow(string change, string action, string target, string? details)
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            var aV1 = await SeedAuditCaseAsync(db);
            var userId = await AddUserAsync(db);

            await ChangeAsync(Catalog(db), change, userId, aV1.Id);

            var audit = Assert.Single(await AuditsAsync(db));
            Assert.Equal((Now, (Guid?)userId, action, target, details), (audit.At, audit.UserId, audit.Action, audit.Target, audit.Details));
        }
    }

    [Theory]
    [InlineData("default")]
    [InlineData("disable")]
    [InlineData("publish")]
    [InlineData("rollback")]
    public async Task Change_FailingAuditWrite_RollsBackTheChange(string change)
    {
        var (db, cs) = await FreshAsync();
        await using (db)
        {
            var aV1 = await SeedAuditCaseAsync(db);
            var userId = await AddUserAsync(db);
            var a2 = (await CurrentAsync(db, "a"))!.Value;
            await using var ctx = new MaldexDbContext(new DbContextOptionsBuilder<MaldexDbContext>().UseSqlServer(cs).AddInterceptors(new FailingAuditInterceptor()).Options);

            await Assert.ThrowsAsync<DbUpdateException>(() => ChangeAsync(Catalog(ctx), change, userId, aV1.Id));

            await using var check = sql.CreateContext(cs);
            Assert.Equal(["a"], await check.Templates.Where(t => t.IsDefault).Select(t => t.Id).ToListAsync(Ct));
            Assert.True((await check.Templates.SingleAsync(t => t.Id == "b", Ct)).Enabled);
            Assert.Equal(a2, await CurrentAsync(db, "a"));
            Assert.Empty(await AuditsAsync(db));
        }
    }

    [Fact]
    public async Task RollBack_ReturnsTheVersionNumbersItApplied()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await SeedAuditCaseAsync(db);
            Assert.Equal(new RollBackResult(2, 1), await Catalog(db).RollBackAsync("a", Ct));
        }
    }

    [Fact]
    public async Task NoOpChange_WritesNoAuditRow()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await SeedAuditCaseAsync(db);
            var userId = await AddUserAsync(db);
            var catalog = Catalog(db);

            await catalog.SetDefaultAsync("a", Ct, new TemplateAudit(userId, "template.default")); // already the default
            await catalog.SetEnabledAsync("b", true, Ct, new TemplateAudit(userId, "template.enable")); // already enabled

            Assert.Empty(await AuditsAsync(db));
        }
    }

    [Theory]
    [InlineData("disable-default")]
    [InlineData("default-disabled")]
    [InlineData("rollback-no-previous")]
    [InlineData("publish-foreign")]
    [InlineData("publish-retired")]
    public async Task RefusedChange_ThrowsTemplateRuleException_AndWritesNothing(string change)
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await SeedAuditCaseAsync(db);
            await AddTemplateAsync(db, "off", enabled: false);
            var retired = await AddVersionAsync(db, "a", 3, TemplateVersionStatus.Retired);
            var bV1 = (await CurrentAsync(db, "b"))!.Value;
            var userId = await AddUserAsync(db);
            var audit = new TemplateAudit(userId, "template.x");
            var catalog = Catalog(db);

            await Assert.ThrowsAsync<TemplateRuleException>(() => change switch
            {
                "disable-default" => catalog.SetEnabledAsync("a", false, Ct, audit),
                "default-disabled" => catalog.SetDefaultAsync("off", Ct, audit),
                "rollback-no-previous" => catalog.RollBackAsync("b", Ct, _ => audit),
                "publish-foreign" => catalog.PublishAsync("a", bV1, Ct, audit),
                "publish-retired" => catalog.PublishAsync("a", retired.Id, Ct, audit),
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            });

            Assert.Empty(await AuditsAsync(db));
            Assert.Equal(TemplateVersionStatus.Ready, (await StatusesAsync(db, "a"))[2]);
        }
    }

    [Fact]
    public async Task Sweep_AuditsEveryDeletedVersion_EvenWhenALaterDeleteFails()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            var good = await AddVersionAsync(db, "a", 1, TemplateVersionStatus.Retired);
            var bad = await AddVersionAsync(db, "a", 2, TemplateVersionStatus.Retired);
            var store = new RecordingStore { FailStems = { bad.FileStem } };

            await Catalog(db, store: store).SweepAsync(Ct);

            var audit = Assert.Single(await AuditsAsync(db));
            Assert.Equal((Now, (Guid?)null, "template.version.delete", good.Id.ToString(), "template=a; v=1"),
                (audit.At, audit.UserId, audit.Action, audit.Target, audit.Details));
        }
    }

    [Fact]
    public async Task Sweep_FailingAuditWrite_LeavesTheVersionRetired()
    {
        var (db, cs) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            await AddVersionAsync(db, "a", 1, TemplateVersionStatus.Retired);
            await using var ctx = new MaldexDbContext(new DbContextOptionsBuilder<MaldexDbContext>().UseSqlServer(cs).AddInterceptors(new FailingAuditInterceptor()).Options);

            Assert.Empty(await Catalog(ctx).SweepAsync(Ct));

            Assert.Equal(TemplateVersionStatus.Retired, (await StatusesAsync(db, "a"))[1]); // the next sweep tries again
            Assert.Empty(await AuditsAsync(db));
        }
    }

    // --- retry policy ---

    private MaldexDbContext ContextWith(string cs, FailingSaveInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<MaldexDbContext>().UseSqlServer(cs).AddInterceptors(interceptor).Options);

    [Fact]
    public async Task Mutation_NonRetryableDbUpdateException_IsNotRetried()
    {
        var (db, cs) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            var interceptor = new FailingSaveInterceptor(_ => new DbUpdateException("some other failure"));
            await using var ctx = ContextWith(cs, interceptor);

            await Assert.ThrowsAsync<DbUpdateException>(() => Catalog(ctx).SetEnabledAsync("a", false, Ct));

            Assert.Equal(1, interceptor.Calls);
        }
    }

    [Fact]
    public async Task Mutation_ConcurrencyConflict_IsRetried()
    {
        var (db, cs) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a");
            var interceptor = new FailingSaveInterceptor(n => n == 1 ? new DbUpdateConcurrencyException("stale") : null);
            await AddVersionAsync(db, "a", 1, current: true);
            await using var ctx = ContextWith(cs, interceptor);

            await Catalog(ctx).SetEnabledAsync("a", false, Ct);

            Assert.Equal(2, interceptor.Calls);
            Assert.Null(await Catalog(db).FindAvailableAsync("a", Ct));
        }
    }

    // --- delete an empty template ---

    private static async Task SeedBuildJobAsync(MaldexDbContext db, string templateId, TemplateJobState state)
    {
        db.TemplateJobs.Add(new TemplateJob
        {
            Id = Guid.NewGuid(), Kind = TemplateJobKind.Build, TemplateId = templateId, State = state,
            RequestedByUserId = await AddUserAsync(db), CreatedAt = Now,
        });
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task DeleteEmpty_RemovesTheTemplateAndItsTerminalJobs_AndAuditsWithIt()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a", isDefault: true);
            await AddTemplateAsync(db, "empty");
            await SeedBuildJobAsync(db, "empty", TemplateJobState.Failed);
            await SeedBuildJobAsync(db, "empty", TemplateJobState.Cancelled);
            await SeedBuildJobAsync(db, "a", TemplateJobState.Failed);

            await Catalog(db).DeleteEmptyAsync("empty", Ct, new TemplateAudit(null, "template.delete"));

            await using var fresh = sql.CreateContext(db.Database.GetConnectionString()!);
            Assert.Equal(["a"], await fresh.Templates.Select(t => t.Id).ToListAsync(Ct));
            Assert.Equal(["a"], await fresh.TemplateJobs.Select(j => j.TemplateId).ToListAsync(Ct));
            var audit = Assert.Single(await fresh.AuditLogs.Where(l => l.Action == "template.delete").ToListAsync(Ct));
            Assert.Equal("empty", audit.Target);
        }
    }

    [Theory]
    [InlineData(TemplateJobState.Queued)]
    [InlineData(TemplateJobState.Installing)]
    [InlineData(TemplateJobState.Provisioning)]
    [InlineData(TemplateJobState.Configuring)]
    public async Task DeleteEmpty_RefusesAnActiveJob_AndChangesNothing(TemplateJobState state)
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a", isDefault: true);
            await AddTemplateAsync(db, "empty");
            await SeedBuildJobAsync(db, "empty", TemplateJobState.Failed);
            await SeedBuildJobAsync(db, "empty", state);

            var ex = await Assert.ThrowsAsync<TemplateRuleException>(() => Catalog(db).DeleteEmptyAsync("empty", Ct, new TemplateAudit(null, "template.delete")));

            Assert.Equal("a job is already running for this template", ex.Message);
            await using var fresh = sql.CreateContext(db.Database.GetConnectionString()!);
            Assert.Equal(2, await fresh.TemplateJobs.CountAsync(j => j.TemplateId == "empty", Ct));
            Assert.Empty(await fresh.AuditLogs.Where(l => l.Action == "template.delete").ToListAsync(Ct));
        }
    }

    [Theory]
    [InlineData(TemplateVersionStatus.Ready)]
    [InlineData(TemplateVersionStatus.Retired)]
    [InlineData(TemplateVersionStatus.Deleted)]
    public async Task DeleteEmpty_RefusesATemplateWithAVersionOfAnyStatus(TemplateVersionStatus status)
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "a", isDefault: true);
            var held = await AddTemplateAsync(db, "held");
            db.TemplateVersions.Add(new TemplateVersion { Id = Guid.NewGuid(), TemplateId = "held", Number = 1, Origin = TemplateVersionOrigin.Edit, FileStem = "held", SealedAt = Now, Status = status });
            await db.SaveChangesAsync(Ct);

            var ex = await Assert.ThrowsAsync<TemplateRuleException>(() => Catalog(db).DeleteEmptyAsync("held", Ct));

            Assert.Equal("the template has versions; it cannot be deleted", ex.Message);
            await using var fresh = sql.CreateContext(db.Database.GetConnectionString()!);
            Assert.True(await fresh.Templates.AnyAsync(t => t.Id == "held", Ct));
        }
    }

    [Fact]
    public async Task DeleteEmpty_RefusesTheDefault()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            await AddTemplateAsync(db, "only", isDefault: true);

            var ex = await Assert.ThrowsAsync<TemplateRuleException>(() => Catalog(db).DeleteEmptyAsync("only", Ct));

            Assert.Equal("choose another default first", ex.Message);
        }
    }
}

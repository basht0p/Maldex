using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Data.Templates;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maldex.Integration.Tests.Data;

[Collection("sql")]
public class TemplateImporterTests(SqlServerFixture sql)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeStore : ITemplateStore
    {
        public Dictionary<string, long?> Present { get; } = new();
        public bool Exists(string fileStem) => Present.ContainsKey(fileStem);
        public long? SizeOf(string fileStem) => Present.GetValueOrDefault(fileStem);
        public Task DeleteAsync(string fileStem, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<MaldexDbContext> FreshAsync()
    {
        var db = sql.CreateContext(sql.CreateDatabaseConnectionString());
        await db.Database.MigrateAsync(Ct);
        return db;
    }

    private static Task<int> ImportAsync(MaldexDbContext db, FakeStore store, params string[] ids) =>
        TemplateImporter.ImportAsync(db, store, ids, new FixedTime(Now), NullLogger.Instance, Ct);

    [Fact]
    public async Task Import_CreatesTemplateAndV1_AsDefault()
    {
        await using var db = await FreshAsync();
        var store = new FakeStore { Present = { ["win11-26h2"] = 4096 } };

        Assert.Equal(1, await ImportAsync(db, store, "win11-26h2"));

        var t = await db.Templates.SingleAsync(Ct);
        Assert.Equal("win11-26h2", t.Id);
        Assert.Equal("win11-26h2", t.DisplayName);
        Assert.True(t.IsDefault);
        Assert.True(t.Enabled);
        Assert.Equal(Now, t.CreatedAt);
        var v = await db.TemplateVersions.SingleAsync(Ct);
        Assert.Equal(t.CurrentVersionId, v.Id);
        Assert.Equal("win11-26h2", v.TemplateId);
        Assert.Equal(1, v.Number);
        Assert.Equal(TemplateVersionOrigin.Imported, v.Origin);
        Assert.Equal("win11-26h2", v.FileStem);
        Assert.Equal(TemplateVersionStatus.Ready, v.Status);
        Assert.Equal(Now, v.SealedAt);
        Assert.Equal(4096, v.SizeBytes);
        Assert.Null(v.WindowsBuild);
        Assert.Null(v.SealedByUserId);
        Assert.Null(v.ParentVersionId);
    }

    [Fact]
    public async Task Import_IsIdempotent()
    {
        await using var db = await FreshAsync();
        var store = new FakeStore { Present = { ["a"] = 1 } };
        Assert.Equal(1, await ImportAsync(db, store, "a"));
        Assert.Equal(0, await ImportAsync(db, store, "a"));
        Assert.Equal(1, await db.Templates.CountAsync(Ct));
        Assert.Equal(1, await db.TemplateVersions.CountAsync(Ct));
    }

    [Fact]
    public async Task Import_SkipsMissingFile_ThenImportsWhenItAppears()
    {
        await using var db = await FreshAsync();
        var store = new FakeStore();
        Assert.Equal(0, await ImportAsync(db, store, "late"));
        Assert.Empty(await db.Templates.ToListAsync(Ct));

        store.Present["late"] = 7;
        Assert.Equal(1, await ImportAsync(db, store, "late"));
        Assert.Equal("late", (await db.Templates.SingleAsync(Ct)).Id);
    }

    [Fact]
    public async Task Import_SecondImage_IsNotDefault()
    {
        await using var db = await FreshAsync();
        var store = new FakeStore { Present = { ["a"] = 1, ["b"] = 2 } };
        Assert.Equal(2, await ImportAsync(db, store, "a", "b"));
        Assert.True((await db.Templates.SingleAsync(t => t.Id == "a", Ct)).IsDefault);
        Assert.False((await db.Templates.SingleAsync(t => t.Id == "b", Ct)).IsDefault);
    }

    [Fact]
    public async Task Import_DoesNotTouchExistingTemplate()
    {
        await using var db = await FreshAsync();
        var existing = new Template { Id = "a", DisplayName = "Custom", Enabled = false, CreatedAt = Now.AddDays(-1) };
        db.Templates.Add(existing);
        await db.SaveChangesAsync(Ct);
        var v = new TemplateVersion
        {
            Id = Guid.NewGuid(), TemplateId = "a", Number = 3, Origin = TemplateVersionOrigin.Edit,
            FileStem = "other-stem", SealedAt = Now, Status = TemplateVersionStatus.Ready,
        };
        db.TemplateVersions.Add(v);
        await db.SaveChangesAsync(Ct);
        existing.CurrentVersionId = v.Id;
        await db.SaveChangesAsync(Ct);

        var store = new FakeStore { Present = { ["a"] = 1 } };
        Assert.Equal(0, await ImportAsync(db, store, "a"));

        var t = await db.Templates.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("Custom", t.DisplayName);
        Assert.False(t.Enabled);
        Assert.Equal(v.Id, t.CurrentVersionId);
        Assert.Equal(1, await db.TemplateVersions.CountAsync(Ct));
    }
}

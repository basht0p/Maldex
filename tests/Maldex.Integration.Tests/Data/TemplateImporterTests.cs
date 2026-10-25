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
        public Task CreateBlankAsync(string stem, long diskBytes, string varsTemplatePath, CancellationToken ct) => throw new NotSupportedException();
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

    private sealed class AllPresentStore : ITemplateStore
    {
        public Task CreateBlankAsync(string stem, long diskBytes, string varsTemplatePath, CancellationToken ct) => throw new NotSupportedException();
        public bool Exists(string fileStem) => true;
        public long? SizeOf(string fileStem) => null;
        public Task DeleteAsync(string fileStem, CancellationToken ct) => Task.CompletedTask;
    }

    private static TemplateCatalog Catalog(MaldexDbContext db, ITemplateStore store) =>
        new(db, store, Microsoft.Extensions.Options.Options.Create(new TemplateOptions()), new FixedTime(Now));

    private static async Task<string> DefaultIdAsync(MaldexDbContext db) =>
        await db.Templates.AsNoTracking().Where(t => t.IsDefault).Select(t => t.Id).SingleAsync(Ct);

    [Fact]
    public async Task Import_FakeThenReal_SameDatabase_MovesDefaultToTemplateWithDisk()
    {
        await using var db = await FreshAsync();
        // Fake mode: every disk "exists", so the configured fake image is imported and becomes the default.
        Assert.Equal(1, await TemplateImporter.ImportAsync(db, new AllPresentStore(), ["win10-ltsc-2021"], new FixedTime(Now), NullLogger.Instance, Ct));
        Assert.Equal("win10-ltsc-2021", await DefaultIdAsync(db));

        // Real mode on the same database: the fake image has no disk, the real one has.
        var real = new FakeStore { Present = { ["win11-26h2"] = 1 } };
        Assert.Equal(1, await ImportAsync(db, real, "win11-26h2"));

        Assert.Equal("win11-26h2", await DefaultIdAsync(db));
        var catalog = Catalog(db, real);
        Assert.Equal("win11-26h2", (await catalog.DefaultAsync(Ct))?.Id);
        Assert.Equal(["win11-26h2"], (await catalog.AvailableAsync(Ct)).Select(t => t.Id));
    }

    [Fact]
    public async Task Import_DefaultDiskMissing_MovesDefaultToExistingEnabledTemplateWithDisk()
    {
        await using var db = await FreshAsync();
        await ImportAsync(db, new FakeStore { Present = { ["a"] = 1, ["b"] = 1, ["c"] = 1 } }, "a", "b", "c");
        (await db.Templates.SingleAsync(t => t.Id == "b", Ct)).Enabled = false;
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        // Nothing new to import; "a" (default) lost its disk, "b" is disabled, "c" is usable.
        Assert.Equal(0, await ImportAsync(db, new FakeStore { Present = { ["b"] = 1, ["c"] = 1 } }, "a", "b", "c"));

        Assert.Equal("c", await DefaultIdAsync(db));
    }

    [Fact]
    public async Task Import_DefaultDiskMissing_NoTemplateWithDisk_KeepsDefault()
    {
        await using var db = await FreshAsync();
        await ImportAsync(db, new FakeStore { Present = { ["a"] = 1, ["b"] = 1 } }, "a", "b");
        db.ChangeTracker.Clear();

        Assert.Equal(0, await ImportAsync(db, new FakeStore(), "a", "b"));

        Assert.Equal("a", await DefaultIdAsync(db));
    }

    [Fact]
    public async Task Import_DefaultDiskPresent_KeepsDefault()
    {
        await using var db = await FreshAsync();
        await ImportAsync(db, new FakeStore { Present = { ["b"] = 1 } }, "b");
        db.ChangeTracker.Clear();

        Assert.Equal(1, await ImportAsync(db, new FakeStore { Present = { ["a"] = 1, ["b"] = 1 } }, "a"));

        Assert.Equal("b", await DefaultIdAsync(db));
    }

    /// <summary>Like <c>LibvirtTemplateStore</c>: a stem that is not a valid slug throws instead of answering.</summary>
    private sealed class InvalidStemStore(string invalid) : ITemplateStore
    {
        public Task CreateBlankAsync(string stem, long diskBytes, string varsTemplatePath, CancellationToken ct) => throw new NotSupportedException();
        public bool Exists(string fileStem) => fileStem == invalid ? throw new ArgumentException("Invalid template file stem.", nameof(fileStem)) : true;
        public long? SizeOf(string fileStem) => null;
        public Task DeleteAsync(string fileStem, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A fake-mode start imported "Odd_Image" (default) and "good"; the next start is on a store that rejects "Odd_Image".</summary>
    private async Task<MaldexDbContext> OddDefaultAsync()
    {
        var db = await FreshAsync();
        Assert.Equal(2, await TemplateImporter.ImportAsync(db, new AllPresentStore(), ["Odd_Image", "good"], new FixedTime(Now), NullLogger.Instance, Ct));
        Assert.Equal("Odd_Image", await DefaultIdAsync(db));
        db.ChangeTracker.Clear();
        return db;
    }

    [Fact]
    public async Task Import_DefaultHasInvalidStem_DoesNotThrow_AndMovesDefaultToUsableTemplate()
    {
        await using var db = await OddDefaultAsync();

        Assert.Equal(0, await TemplateImporter.ImportAsync(db, new InvalidStemStore("Odd_Image"), ["Odd_Image", "good"], new FixedTime(Now), NullLogger.Instance, Ct));

        Assert.Equal("good", await DefaultIdAsync(db));
    }

    [Fact]
    public async Task Import_MovingTheDefault_WritesOneSystemAuditRow()
    {
        await using var db = await OddDefaultAsync();
        var oldId = "Odd_Image";

        await TemplateImporter.ImportAsync(db, new InvalidStemStore(oldId), [oldId, "good"], new FixedTime(Now), NullLogger.Instance, Ct);
        await TemplateImporter.ImportAsync(db, new InvalidStemStore(oldId), [oldId, "good"], new FixedTime(Now), NullLogger.Instance, Ct); // default now usable: moves nothing

        var row = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "template.default", Ct);
        Assert.Null(row.UserId);
        Assert.Equal("good", row.Target);
        Assert.Equal($"from={oldId}; reason=disk missing", row.Details);
        Assert.Equal(Now, row.At);
    }

    [Fact]
    public async Task Import_DefaultUsable_WritesNoAuditRow()
    {
        await using var db = await FreshAsync();
        await ImportAsync(db, new FakeStore { Present = { ["a"] = 1 } }, "a");
        await ImportAsync(db, new FakeStore { Present = { ["a"] = 1 } }, "a");

        Assert.Empty(await db.AuditLogs.AsNoTracking().Where(a => a.Action == "template.default").ToListAsync(Ct));
    }

    [Fact]
    public async Task Import_IdNotAValidFileStem_IsSkipped_AndOthersImported()
    {
        var dir = Directory.CreateTempSubdirectory("mx-import-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "win11-26h2.qcow2"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "win11-26h2.vars.fd"), [1]);
            var store = new Maldex.Infrastructure.Libvirt.LibvirtTemplateStore(
                Microsoft.Extensions.Options.Options.Create(new Maldex.Infrastructure.Libvirt.LibvirtOptions { BaseImageDir = dir }),
                new Maldex.Infrastructure.Execution.ProcessRunner());
            await using var db = await FreshAsync();

            var imported = await TemplateImporter.ImportAsync(db, store, ["Win_11", "win11-26h2"], new FixedTime(Now), NullLogger.Instance, Ct);

            Assert.Equal(1, imported);
            Assert.Equal(["win11-26h2"], await db.Templates.Select(t => t.Id).ToListAsync(Ct));
        }
        finally { Directory.Delete(dir, true); }
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

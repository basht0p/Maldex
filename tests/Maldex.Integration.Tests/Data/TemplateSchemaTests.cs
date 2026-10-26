using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;
using OrgEntity = Maldex.Core.Domain.Org;

namespace Maldex.Integration.Tests.Data;

[Collection("sql")]
public class TemplateSchemaTests(SqlServerFixture sql)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Template NewTemplate(string id, bool isDefault = false) => new()
    {
        Id = id, DisplayName = "Template " + id, IsDefault = isDefault, Enabled = true, CreatedAt = Now,
    };

    private static TemplateVersion NewVersion(string templateId, int number) => new()
    {
        Id = Guid.NewGuid(), TemplateId = templateId, Number = number, Origin = TemplateVersionOrigin.IsoBuild,
        FileStem = Guid.NewGuid().ToString("N"), SealedAt = Now, Status = TemplateVersionStatus.Ready,
    };

    private async Task<(MaldexDbContext db, string cs)> FreshAsync()
    {
        var cs = sql.CreateDatabaseConnectionString();
        var db = sql.CreateContext(cs);
        await db.Database.MigrateAsync(Ct);
        return (db, cs);
    }

    [Fact]
    public async Task Template_RoundTrips_WithVersion()
    {
        var (db, cs) = await FreshAsync();
        var parent = NewVersion("win11-golden", 1);
        var parentId = parent.Id;
        var v = NewVersion("win11-golden", 2);
        v.ParentVersionId = parentId;
        v.Origin = TemplateVersionOrigin.Edit;
        v.WindowsEdition = "Windows 11 Pro";
        v.WindowsBuild = "26200.1";
        v.OfficePresent = true;
        v.AgentVersion = "1.2.3";
        v.SysmonConfigSha256 = new string('a', 64);
        v.MitmCaSha256 = new string('b', 64);
        v.SourceMediaIds = "[\"m1\"]";
        v.Note = "note";
        v.SizeBytes = 123456789012;
        var t = NewTemplate("win11-golden", isDefault: true);
        t.Description = "desc";
        await using (db)
        {
            db.Templates.Add(t);
            await db.SaveChangesAsync(Ct);
            db.TemplateVersions.AddRange(parent, v);
            await db.SaveChangesAsync(Ct);
            t.CurrentVersionId = v.Id;
            await db.SaveChangesAsync(Ct);
        }
        await using var db2 = sql.CreateContext(cs);
        var rt = await db2.Templates.SingleAsync(x => x.Id == "win11-golden", Ct);
        var rv = await db2.TemplateVersions.SingleAsync(x => x.Id == v.Id, Ct);
        Assert.Equal("Template win11-golden", rt.DisplayName);
        Assert.Equal("desc", rt.Description);
        Assert.True(rt.IsDefault);
        Assert.True(rt.Enabled);
        Assert.Equal(v.Id, rt.CurrentVersionId);
        Assert.Equal(Now, rt.CreatedAt);
        Assert.NotEmpty(rt.RowVersion);
        Assert.Equal("win11-golden", rv.TemplateId);
        Assert.Equal(2, rv.Number);
        Assert.Equal(parentId, rv.ParentVersionId);
        Assert.Equal(TemplateVersionOrigin.Edit, rv.Origin);
        Assert.Equal(v.FileStem, rv.FileStem);
        Assert.Equal("Windows 11 Pro", rv.WindowsEdition);
        Assert.Equal("26200.1", rv.WindowsBuild);
        Assert.True(rv.OfficePresent);
        Assert.Equal("1.2.3", rv.AgentVersion);
        Assert.Equal(new string('a', 64), rv.SysmonConfigSha256);
        Assert.Equal(new string('b', 64), rv.MitmCaSha256);
        Assert.Equal("[\"m1\"]", rv.SourceMediaIds);
        Assert.Null(rv.SealedByUserId);
        Assert.Equal(Now, rv.SealedAt);
        Assert.Equal("note", rv.Note);
        Assert.Equal(TemplateVersionStatus.Ready, rv.Status);
        Assert.Equal(123456789012, rv.SizeBytes);
    }

    [Fact]
    public async Task OnlyOneDefault_IsEnforced()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            db.Templates.AddRange(NewTemplate("a", isDefault: true), NewTemplate("b", isDefault: false));
            await db.SaveChangesAsync(Ct);

            db.Templates.Add(NewTemplate("c", isDefault: true));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task VersionNumber_IsUniquePerTemplate()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            db.Templates.AddRange(NewTemplate("a"), NewTemplate("b"));
            await db.SaveChangesAsync(Ct);
            db.TemplateVersions.AddRange(NewVersion("a", 1), NewVersion("b", 1));
            await db.SaveChangesAsync(Ct);

            db.TemplateVersions.Add(NewVersion("a", 1));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task FileStem_IsUnique_AcrossTemplates()
    {
        var (db, _) = await FreshAsync();
        await using (db)
        {
            db.Templates.AddRange(NewTemplate("a"), NewTemplate("b"));
            await db.SaveChangesAsync(Ct);
            var first = NewVersion("a", 1);
            db.TemplateVersions.Add(first);
            await db.SaveChangesAsync(Ct);

            var twin = NewVersion("b", 1);
            twin.FileStem = first.FileStem; // two versions must never share one disk
            db.TemplateVersions.Add(twin);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("IX_TemplateVersions_FileStem", ex.InnerException?.Message);
        }
    }

    [Fact]
    public async Task Task_TemplateVersionId_IsOptional()
    {
        var (db, cs) = await FreshAsync();
        var org = new OrgEntity { Id = Guid.NewGuid(), Name = "Acme-" + Guid.NewGuid().ToString("N"), RetentionDays = 30 };
        var user = TestUsers.Create(UserRole.Analyst, "A");
        var task = new AnalysisTask
        {
            Id = Guid.NewGuid(), OrgId = org.Id, SubmittedByUserId = user.Id, Url = "http://x.test",
            Image = "win10", NetworkMode = NetworkMode.Simulated, DurationSec = 300, CreatedAt = Now,
        };
        await using (db)
        {
            db.AddRange(org, user, task);
            await db.SaveChangesAsync(Ct);
        }
        await using var db2 = sql.CreateContext(cs);
        Assert.Null((await db2.Tasks.SingleAsync(x => x.Id == task.Id, Ct)).TemplateVersionId);
    }
}

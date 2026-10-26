using Maldex.Core.Domain;
using Maldex.Core.Lifecycle;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;
using OrgEntity = Maldex.Core.Domain.Org;

namespace Maldex.Integration.Tests.Data;

[Collection("sql")]
public class PersistenceTests(SqlServerFixture sql)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sha(char c) => new(c, 64);

    private static (OrgEntity org, AppUser user, Sample sample) Seed(MaldexDbContext db)
    {
        var org = new OrgEntity { Id = Guid.NewGuid(), Name = "Acme-" + Guid.NewGuid().ToString("N"), RetentionDays = 30 };
        var user = TestUsers.Create(UserRole.Analyst, "A");
        var sample = new Sample { Sha256 = Sha('a'), Sha1 = new string('b', 40), Md5 = new string('c', 32), Size = 5, BlobKey = "k", FirstSeen = Now };
        db.AddRange(org, user, sample);
        return (org, user, sample);
    }

    private static AnalysisTask NewTask(OrgEntity o, AppUser u, Sample s) => new()
    {
        Id = Guid.NewGuid(), OrgId = o.Id, SubmittedByUserId = u.Id, SampleSha256 = s.Sha256,
        Image = "win10", NetworkMode = NetworkMode.Simulated, DurationSec = 300, CreatedAt = Now,
    };

    [Fact]
    public async Task Task_RoundTrips_WithHistoryAndWarnings()
    {
        var cs = sql.CreateDatabaseConnectionString();
        Guid id;
        await using (var db = sql.CreateContext(cs))
        {
            await db.Database.MigrateAsync(Ct);
            var (o, u, s) = Seed(db);
            var t = NewTask(o, u, s);
            t.ProcessingWarnings.Add("yara timeout");
            t.TransitionTo(RunStatus.Preparing, "picked up", Now);
            db.Tasks.Add(t);
            await db.SaveChangesAsync(Ct);
            id = t.Id;
        }
        await using (var db = sql.CreateContext(cs))
        {
            var t = await db.Tasks.Include(x => x.StatusHistory).SingleAsync(x => x.Id == id, Ct);
            Assert.Equal(["yara timeout"], t.ProcessingWarnings);
            Assert.Equal(RunStatus.Preparing, t.Status);
            var h = Assert.Single(t.StatusHistory);
            Assert.Equal(id, h.TaskId);
            Assert.Equal(RunStatus.Queued, h.From);
            Assert.Equal(RunStatus.Preparing, h.To);
            Assert.NotEmpty(t.RowVersion);
        }
    }

    [Fact]
    public async Task ProcessingWarnings_InPlaceMutation_IsPersisted()
    {
        var cs = sql.CreateDatabaseConnectionString();
        Guid id;
        await using (var db = sql.CreateContext(cs))
        {
            await db.Database.MigrateAsync(Ct);
            var (o, u, s) = Seed(db);
            var t = NewTask(o, u, s);
            db.Tasks.Add(t);
            await db.SaveChangesAsync(Ct);
            id = t.Id;
            t.ProcessingWarnings.Add("late");
            await db.SaveChangesAsync(Ct);
        }
        await using (var db = sql.CreateContext(cs))
            Assert.Equal(["late"], (await db.Tasks.SingleAsync(x => x.Id == id, Ct)).ProcessingWarnings);
    }

    [Fact]
    public async Task Sample_DuplicateSha_Throws_DbUpdateException()
    {
        var cs = sql.CreateDatabaseConnectionString();
        await using var db = sql.CreateContext(cs);
        await db.Database.MigrateAsync(Ct);
        db.Samples.Add(new Sample { Sha256 = Sha('d'), Sha1 = new string('1', 40), Md5 = new string('2', 32), BlobKey = "k", FirstSeen = Now });
        await db.SaveChangesAsync(Ct);
        await using var db2 = sql.CreateContext(cs);
        db2.Samples.Add(new Sample { Sha256 = Sha('d'), Sha1 = new string('1', 40), Md5 = new string('2', 32), BlobKey = "k", FirstSeen = Now });
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Task_ConcurrentUpdate_ThrowsConcurrencyException()
    {
        var cs = sql.CreateDatabaseConnectionString();
        Guid id;
        await using (var db = sql.CreateContext(cs))
        {
            await db.Database.MigrateAsync(Ct);
            var (o, u, s) = Seed(db);
            var t = NewTask(o, u, s);
            db.Tasks.Add(t);
            await db.SaveChangesAsync(Ct);
            id = t.Id;
        }
        await using var a = sql.CreateContext(cs);
        await using var b = sql.CreateContext(cs);
        var ta = await a.Tasks.SingleAsync(x => x.Id == id, Ct);
        var tb = await b.Tasks.SingleAsync(x => x.Id == id, Ct);
        ta.Attempt = 1;
        await a.SaveChangesAsync(Ct);
        tb.Attempt = 2;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Org_DuplicateName_Throws()
    {
        var cs = sql.CreateDatabaseConnectionString();
        await using var db = sql.CreateContext(cs);
        await db.Database.MigrateAsync(Ct);
        db.Orgs.Add(new OrgEntity { Id = Guid.NewGuid(), Name = "Dup", RetentionDays = 1 });
        await db.SaveChangesAsync(Ct);
        db.Orgs.Add(new OrgEntity { Id = Guid.NewGuid(), Name = "Dup", RetentionDays = 1 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task CreateMigratedContext_Works()
    {
        await using var db = await sql.CreateMigratedContextAsync();
        Assert.Empty(await db.Tasks.ToListAsync(Ct));
    }
}

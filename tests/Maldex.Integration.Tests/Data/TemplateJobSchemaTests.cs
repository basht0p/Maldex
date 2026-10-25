using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Maldex.Integration.Tests.Data;

[Collection("sql")]
public class TemplateJobSchemaTests(SqlServerFixture sql)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TemplateJob NewJob(string templateId, Guid baseVersionId, Guid userId, TemplateJobState state = TemplateJobState.Queued) => new()
    {
        Id = Guid.NewGuid(), Kind = TemplateJobKind.Edit, TemplateId = templateId, BaseVersionId = baseVersionId,
        Network = NetworkMode.Simulated, State = state, RequestedByUserId = userId, CreatedAt = Now,
    };

    /// <summary>A fresh database with template "t1" (one version) and a user.</summary>
    private async Task<(MaldexDbContext db, Guid versionId, Guid userId)> SeedAsync()
    {
        var db = sql.CreateContext(sql.CreateDatabaseConnectionString());
        await db.Database.MigrateAsync(Ct);
        var user = new AppUser { Id = Guid.NewGuid(), EntraObjectId = Guid.NewGuid().ToString("N"), DisplayName = "A", Role = UserRole.Admin };
        var template = new Template { Id = "t1", DisplayName = "T1", Enabled = true, CreatedAt = Now };
        var version = new TemplateVersion
        {
            Id = Guid.NewGuid(), TemplateId = "t1", Number = 1, Origin = TemplateVersionOrigin.IsoBuild,
            FileStem = Guid.NewGuid().ToString("N"), SealedAt = Now,
        };
        db.AddRange(user, template);
        await db.SaveChangesAsync(Ct);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync(Ct);
        return (db, version.Id, user.Id);
    }

    [Fact]
    public async Task Job_RoundTrips()
    {
        var (db, versionId, userId) = await SeedAsync();
        await using (db)
        {
            var job = NewJob("t1", versionId, userId);
            job.Kind = TemplateJobKind.Refresh;
            job.Note = "n";
            job.PublishOnSuccess = false;
            db.TemplateJobs.Add(job);
            await db.SaveChangesAsync(Ct);
            var rt = await db.TemplateJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct);
            Assert.Equal(TemplateJobKind.Refresh, rt.Kind);
            Assert.Equal(TemplateJobState.Queued, rt.State);
            Assert.Equal(NetworkMode.Simulated, rt.Network);
            Assert.False(rt.PublishOnSuccess);
            Assert.Equal(Now, rt.CreatedAt);
            Assert.NotEmpty(rt.RowVersion);
        }
    }

    [Fact]
    public async Task OneActiveJobIndex_FiltersOutExactlyTheStateMachinesTerminalStates()
    {
        var (db, _, _) = await SeedAsync();
        await using (db)
        {
            var index = db.Model.FindEntityType(typeof(TemplateJob))!.GetIndexes().Single(i => i.GetDatabaseName() == "IX_TemplateJobs_TemplateId_Active");
            var expected = string.Join(" AND ", Maldex.Core.Lifecycle.TemplateJobStateMachine.TerminalStates.Select(s => $"[State] <> '{s}'"));
            Assert.Equal(expected, index.GetFilter());
        }
    }

    [Fact]
    public async Task OneActiveJobPerTemplate_IsEnforced()
    {
        var (db, versionId, userId) = await SeedAsync();
        await using (db)
        {
            db.TemplateJobs.Add(NewJob("t1", versionId, userId, TemplateJobState.Configuring));
            await db.SaveChangesAsync(Ct);
            db.TemplateJobs.Add(NewJob("t1", versionId, userId));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Theory]
    [InlineData(TemplateJobState.Published)]
    [InlineData(TemplateJobState.Sealed)]
    [InlineData(TemplateJobState.Failed)]
    [InlineData(TemplateJobState.Cancelled)]
    public async Task TerminalJobs_DoNotBlockANewActiveJob(TemplateJobState terminal)
    {
        var (db, versionId, userId) = await SeedAsync();
        await using (db)
        {
            db.TemplateJobs.AddRange(NewJob("t1", versionId, userId, terminal), NewJob("t1", versionId, userId, terminal), NewJob("t1", versionId, userId));
            await db.SaveChangesAsync(Ct);
            Assert.Equal(3, await db.TemplateJobs.CountAsync(Ct));
        }
    }

    [Fact]
    public async Task JobStates_AreStoredAsStrings()
    {
        var (db, versionId, userId) = await SeedAsync();
        await using (db)
        {
            db.TemplateJobs.Add(NewJob("t1", versionId, userId, TemplateJobState.Verifying));
            await db.SaveChangesAsync(Ct);
            var raw = await db.Database.SqlQueryRaw<string>("SELECT State AS Value FROM TemplateJobs").SingleAsync(Ct);
            Assert.Equal("Verifying", raw);
        }
    }
}

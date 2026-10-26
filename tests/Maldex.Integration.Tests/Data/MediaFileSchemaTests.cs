using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Maldex.Integration.Tests.Data;

[Collection("sql")]
public class MediaFileSchemaTests(SqlServerFixture sql)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MediaFile NewMedia(Guid userId, string? sha256, MediaStatus status) => new()
    {
        Id = Guid.NewGuid(), Kind = MediaKind.OsInstaller, FileName = "win11.iso", Size = 5_000_000_000, ReceivedBytes = 5_000_000_000,
        Sha256 = sha256, Source = MediaSource.Upload, Status = status, AddedByUserId = userId, AddedAt = Now, LastActivityAt = Now,
    };

    private async Task<(MaldexDbContext db, Guid userId)> SeedAsync()
    {
        var db = sql.CreateContext(sql.CreateDatabaseConnectionString());
        await db.Database.MigrateAsync(Ct);
        var user = TestUsers.Create(UserRole.Admin, "A");
        db.Add(user);
        await db.SaveChangesAsync(Ct);
        return (db, user.Id);
    }

    [Fact]
    public async Task MediaFile_RoundTrips()
    {
        var (db, userId) = await SeedAsync();
        await using (db)
        {
            var media = NewMedia(userId, new string('a', 64), MediaStatus.Ready);
            media.Kind = MediaKind.Application;
            media.Source = MediaSource.Url;
            media.SourceUrl = "https://example.test/office.iso";
            media.Contents = "{\"editions\":[{\"index\":1,\"name\":\"Windows 11 Pro\"}]}";
            media.FailureReason = "none";
            media.ReceivedBytes = 42;
            db.MediaFiles.Add(media);
            await db.SaveChangesAsync(Ct);

            var rt = await db.MediaFiles.AsNoTracking().SingleAsync(m => m.Id == media.Id, Ct);
            Assert.Equal((media.Kind, media.FileName, media.Size, media.ReceivedBytes, media.Sha256, media.Source, media.SourceUrl, media.Contents, media.Status, media.FailureReason, media.AddedByUserId, media.AddedAt, media.LastActivityAt),
                (rt.Kind, rt.FileName, rt.Size, rt.ReceivedBytes, rt.Sha256, rt.Source, rt.SourceUrl, rt.Contents, rt.Status, rt.FailureReason, rt.AddedByUserId, rt.AddedAt, rt.LastActivityAt));
            Assert.NotEmpty(rt.RowVersion);
            Assert.Equal("Application", await db.Database.SqlQueryRaw<string>("SELECT Kind AS Value FROM MediaFiles").SingleAsync(Ct));
            Assert.Equal("Url", await db.Database.SqlQueryRaw<string>("SELECT Source AS Value FROM MediaFiles").SingleAsync(Ct));
            Assert.Equal("Ready", await db.Database.SqlQueryRaw<string>("SELECT Status AS Value FROM MediaFiles").SingleAsync(Ct));
        }
    }

    [Fact]
    public async Task ReadySha256_IsUnique_ButFailedDuplicatesAreAllowed()
    {
        var (db, userId) = await SeedAsync();
        await using (db)
        {
            var sha = new string('c', 64);
            db.MediaFiles.AddRange(NewMedia(userId, sha, MediaStatus.Ready), NewMedia(userId, sha, MediaStatus.Failed), NewMedia(userId, sha, MediaStatus.Failed), NewMedia(userId, sha, MediaStatus.Deleted));
            await db.SaveChangesAsync(Ct);

            db.MediaFiles.Add(NewMedia(userId, sha, MediaStatus.Ready));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains(Assert.IsType<SqlException>(ex.InnerException).Number, new[] { 2601, 2627 });
        }
    }

    [Fact]
    public async Task ReceivingFiles_WithoutAHash_DoNotCollide()
    {
        var (db, userId) = await SeedAsync();
        await using (db)
        {
            db.MediaFiles.AddRange(NewMedia(userId, null, MediaStatus.Receiving), NewMedia(userId, null, MediaStatus.Receiving));
            await db.SaveChangesAsync(Ct);
            Assert.Equal(2, await db.MediaFiles.CountAsync(Ct));
        }
    }
}

using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Maldex.Integration.Tests.Data;

[Collection("sql")]
public class LocalAuthSchemaTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(MaldexDbContext db, string cs)> FreshAsync()
    {
        var cs = sql.CreateDatabaseConnectionString();
        var db = sql.CreateContext(cs);
        await db.Database.MigrateAsync(Ct);
        return (db, cs);
    }

    [Fact]
    public async Task User_name_is_unique_case_insensitively()
    {
        var (db, _) = await FreshAsync();
        var a = TestUsers.Create(UserRole.Analyst);
        a.UserName = "alice"; a.NormalizedUserName = "ALICE";
        var b = TestUsers.Create(UserRole.Analyst);
        b.UserName = "ALICE"; b.NormalizedUserName = "ALICE";
        db.Users.Add(a);
        await db.SaveChangesAsync(Ct);
        db.Users.Add(b);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Role_is_nullable_and_round_trips_with_kind_and_flags()
    {
        var (db, cs) = await FreshAsync();
        var u = TestUsers.Create(null, "Sso person", UserKind.Sso);
        u.IsBreakGlass = true;
        u.MustChangePassword = true;
        db.Users.Add(u);
        await db.SaveChangesAsync(Ct);

        await using var fresh = sql.CreateContext(cs);
        var back = await fresh.Users.SingleAsync(x => x.Id == u.Id, Ct);
        Assert.Null(back.Role);
        Assert.Equal(UserKind.Sso, back.Kind);
        Assert.True(back.IsBreakGlass);
        Assert.True(back.MustChangePassword);
        Assert.False(back.IsDisabled);
        Assert.Equal("Sso person", back.DisplayName);
    }

    [Fact]
    public async Task System_settings_has_one_seeded_row()
    {
        var (db, _) = await FreshAsync();
        var row = await db.SystemSettings.SingleAsync(Ct);
        Assert.Equal(1, row.Id);
        Assert.False(row.RequireTotpForLocal);
    }

    [Fact]
    public async Task Legacy_entra_rows_become_disabled_sso_users()
    {
        var cs = sql.CreateDatabaseConnectionString();
        await using var db = sql.CreateContext(cs);
        var migrator = db.GetService<IMigrator>();
        var before = db.GetService<IMigrationsAssembly>().Migrations.Keys.Order().TakeWhile(k => !k.EndsWith("_LocalAuth")).Last();
        await migrator.MigrateAsync(before, Ct);

        var id = Guid.NewGuid();
        await using (var conn = new SqlConnection(cs))
        {
            await conn.OpenAsync(Ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Users (Id, EntraObjectId, DisplayName, Role) VALUES (@id, '0123456789abcdefXYZ', 'Old', 'Analyst')";
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        await migrator.MigrateAsync(null, Ct);

        await using var fresh = sql.CreateContext(cs);
        var u = await fresh.Users.SingleAsync(x => x.Id == id, Ct);
        Assert.Equal("legacy-0123456789ab", u.UserName);
        Assert.Equal("LEGACY-0123456789AB", u.NormalizedUserName);
        Assert.True(u.IsDisabled);
        Assert.Equal(UserKind.Sso, u.Kind);
        Assert.Equal(UserRole.Analyst, u.Role);
        Assert.Equal("Old", u.DisplayName);
        Assert.False(string.IsNullOrEmpty(u.SecurityStamp));
        Assert.True(u.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Identity_tables_have_maldex_names()
    {
        var (_, cs) = await FreshAsync();
        var tables = new List<string>();
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES";
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        while (await r.ReadAsync(Ct)) tables.Add(r.GetString(0));
        Assert.Contains("Users", tables);
        Assert.Contains("UserLogins", tables);
        Assert.Contains("UserTokens", tables);
        Assert.Contains("UserClaims", tables);
        Assert.Contains("SystemSettings", tables);
        Assert.DoesNotContain(tables, t => t.StartsWith("AspNet"));
    }
}

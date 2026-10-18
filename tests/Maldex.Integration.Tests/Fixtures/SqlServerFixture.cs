using Maldex.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

namespace Maldex.Integration.Tests.Fixtures;

/// <summary>One SQL Server 2022 container shared by every test in the "sql" collection.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "2048")
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public string CreateDatabaseConnectionString()
    {
        var builder = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = $"maldex_{Guid.NewGuid():N}",
        };
        return builder.ConnectionString;
    }

    public async Task<MaldexDbContext> CreateMigratedContextAsync()
    {
        var options = new DbContextOptionsBuilder<MaldexDbContext>()
            .UseSqlServer(CreateDatabaseConnectionString())
            .Options;
        var context = new MaldexDbContext(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return context;
    }

    public MaldexDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MaldexDbContext>().UseSqlServer(connectionString).Options);
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>;

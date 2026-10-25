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
        .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "4096")
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _drops;
        await _container.DisposeAsync();
    }

    /// <summary>
    /// How many of the most recent test databases are kept. Older ones are dropped: every database holds memory in the
    /// container's 4 GB pool, and with a couple of hundred the server starts refusing queries ("insufficient system memory").
    /// Tests in the "sql" collection run one at a time and none uses more than a few databases, so the oldest are done.
    /// </summary>
    private const int KeptDatabases = 40;

    private readonly Queue<string> _databases = new();

    public string CreateDatabaseConnectionString()
    {
        var builder = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = $"maldex_{Guid.NewGuid():N}",
        };
        lock (_databases)
        {
            _databases.Enqueue(builder.ConnectionString);
            while (_databases.Count > KeptDatabases)
            {
                // In the background, one at a time: a drop takes a while and no test waits for it. Best effort.
                var old = _databases.Dequeue();
                _drops = _drops.ContinueWith(_ => DropDatabase(old), TaskScheduler.Default);
            }
        }
        return builder.ConnectionString;
    }

    private Task _drops = Task.CompletedTask;

    private void DropDatabase(string connectionString)
    {
        var name = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        using (var pooled = new SqlConnection(connectionString)) SqlConnection.ClearPool(pooled);
        using var master = new SqlConnection(_container.GetConnectionString());
        try { master.Open(); } catch (SqlException) { return; }
        using var drop = master.CreateCommand();
        // The name is generated above (maldex_ + 32 hex), never test input.
        drop.CommandText = $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
        try
        {
            drop.ExecuteNonQuery();
        }
        catch (SqlException)
        {
            // Best effort: a database that cannot be dropped now only costs memory.
        }
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

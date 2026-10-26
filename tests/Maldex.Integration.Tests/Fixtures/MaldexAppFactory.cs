using System.Net.Http.Headers;
using Maldex.Core.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Maldex.Integration.Tests.Fixtures;

/// <summary>
/// Boots Maldex.Web against a fresh database from <see cref="SqlServerFixture"/> (one per factory instance)
/// with Fake mode, dev auth (or <c>authMode: "Local"</c>), a temporary artifact root and a temporary key ring. Workers are off unless <c>workers</c> is true. Pass
/// <c>connectionString</c> to boot a second host on an existing factory's database.
/// </summary>
public class MaldexAppFactory : WebApplicationFactory<Program>
{
    private readonly SqlServerFixture _sql;
    private readonly string _environment;
    private readonly bool _workers;
    private readonly string _authMode;
    private readonly string? _devRole;
    private readonly IReadOnlyDictionary<string, string?>? _settings;

    public string ArtifactRoot { get; } = Path.Combine(Path.GetTempPath(), "maldex-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>The Data Protection key ring (Maldex:Auth:KeysDir): a fresh temporary directory per factory, removed on dispose.</summary>
    public string KeysDir { get; } = Path.Combine(Path.GetTempPath(), "maldex-keys-" + Guid.NewGuid().ToString("N"), "keys");
    public string ConnectionString { get; }

    public MaldexAppFactory(SqlServerFixture sql, string environment = "Testing", bool workers = false, string authMode = "Dev", string? devRole = null,
        IReadOnlyDictionary<string, string?>? settings = null, string? connectionString = null)
    {
        _settings = settings;
        _sql = sql;
        _environment = environment;
        _workers = workers;
        _authMode = authMode;
        _devRole = devRole;
        ConnectionString = connectionString ?? sql.CreateDatabaseConnectionString();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("Maldex:Mode", "Fake");
        builder.UseSetting("Maldex:Auth:Mode", _authMode);
        if (_devRole is not null) builder.UseSetting("Maldex:Auth:DevRole", _devRole);
        builder.UseSetting("Maldex:Workers:Enabled", _workers ? "true" : "false");
        builder.UseSetting("Maldex:Artifacts:Root", ArtifactRoot);
        builder.UseSetting("ConnectionStrings:Maldex", ConnectionString);
        builder.UseSetting("Maldex:Auth:KeysDir", KeysDir);
        // Sign-in tests log in many times from the one test-server "IP"; the rate-limit test sets its own limit.
        builder.UseSetting("Maldex:Auth:LoginRateLimitPerMinute", "1000");
        if (_settings is not null)
            foreach (var (key, value) in _settings) builder.UseSetting(key, value);
    }

    /// <summary>Header every mutating /api and /hubs request must carry; <see cref="ConfigureClient"/> adds it to every client.</summary>
    public const string CsrfHeader = "X-Maldex-Csrf";

    /// <summary>Every client from <c>CreateClient</c> sends <c>X-Maldex-Csrf: 1</c>, as the SPA does. Remove it to test the CSRF refusal.</summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add(CsrfHeader, "1");
    }

    public HttpClient CreateClient(UserRole role, string user = "tester")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Role", role.ToString());
        client.DefaultRequestHeaders.Add("X-Dev-User", user);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(ArtifactRoot))
        {
            try { Directory.Delete(ArtifactRoot, recursive: true); } catch (IOException) { }
        }
        var keysParent = Path.GetDirectoryName(KeysDir)!;
        if (disposing && Directory.Exists(keysParent))
        {
            try { Directory.Delete(keysParent, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Same as <see cref="MaldexAppFactory"/> but with the background workers enabled.</summary>
public sealed class MaldexAppFactoryWithWorkers(SqlServerFixture sql) : MaldexAppFactory(sql, workers: true);

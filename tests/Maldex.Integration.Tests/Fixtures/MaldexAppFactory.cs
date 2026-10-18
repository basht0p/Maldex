using System.Net.Http.Headers;
using Maldex.Core.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Maldex.Integration.Tests.Fixtures;

/// <summary>
/// Boots Maldex.Web against a fresh database from <see cref="SqlServerFixture"/> (one per factory instance)
/// with Fake mode, dev auth and a temporary artifact root. Workers are off unless <c>workers</c> is true.
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
    public string ConnectionString { get; }

    public MaldexAppFactory(SqlServerFixture sql, string environment = "Testing", bool workers = false, string authMode = "Dev", string? devRole = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        _settings = settings;
        _sql = sql;
        _environment = environment;
        _workers = workers;
        _authMode = authMode;
        _devRole = devRole;
        ConnectionString = sql.CreateDatabaseConnectionString();
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
        if (_settings is not null)
            foreach (var (key, value) in _settings) builder.UseSetting(key, value);
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
    }
}

/// <summary>Same as <see cref="MaldexAppFactory"/> but with the background workers enabled.</summary>
public sealed class MaldexAppFactoryWithWorkers(SqlServerFixture sql) : MaldexAppFactory(sql, workers: true);

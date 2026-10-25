using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

[Collection("sql")]
public sealed class AuthAndOrgTests(SqlServerFixture sql) : IDisposable
{
    private readonly MaldexAppFactory _factory = new(sql);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Me_ReturnsDevUser()
    {
        using var client = _factory.CreateClient();
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me", Ct);
        Assert.Equal("Dev User", me.GetProperty("displayName").GetString());
        Assert.Equal("Admin", me.GetProperty("role").GetString());
        Assert.NotEqual(Guid.Empty, me.GetProperty("id").GetGuid());
        Assert.Equal(268_435_456, me.GetProperty("maxUploadBytes").GetInt64()); // the effective submit cap

        using var named = _factory.CreateClient(UserRole.Analyst, "alice");
        var alice = await named.GetFromJsonAsync<JsonElement>("/api/me", Ct);
        Assert.Equal("alice", alice.GetProperty("displayName").GetString());
        Assert.Equal("Analyst", alice.GetProperty("role").GetString());
        Assert.NotEqual(me.GetProperty("id").GetGuid(), alice.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Analyst_CannotCreateOrg_403()
    {
        using var client = _factory.CreateClient(UserRole.Analyst);
        var resp = await client.PostAsJsonAsync("/api/orgs", new { name = "Acme", retentionDays = 30 }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var list = await client.GetAsync("/api/orgs", Ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task Admin_CreatesOrg_201_AndAudits()
    {
        using var client = _factory.CreateClient(UserRole.Admin);
        var resp = await client.PostAsJsonAsync("/api/orgs", new { name = "  Acme  ", retentionDays = 30 }, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var org = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = org.GetProperty("id").GetGuid();
        Assert.Equal("Acme", org.GetProperty("name").GetString());
        Assert.Equal($"/api/orgs/{id}", resp.Headers.Location?.OriginalString);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "org.create", Ct);
        Assert.Equal(id.ToString(), audit.Target);
        Assert.NotNull(audit.UserId);

        var put = await client.PutAsJsonAsync($"/api/orgs/{id}", new { name = "Acme2", retentionDays = 10 }, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "org.update" && a.Target == id.ToString(), Ct));

        var missing = await client.PutAsJsonAsync($"/api/orgs/{Guid.NewGuid()}", new { name = "X", retentionDays = 10 }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task DuplicateOrgName_409()
    {
        using var client = _factory.CreateClient(UserRole.Admin);
        (await client.PostAsJsonAsync("/api/orgs", new { name = "Dup", retentionDays = 30 }, Ct)).EnsureSuccessStatusCode();
        var resp = await client.PostAsJsonAsync("/api/orgs", new { name = "dup", retentionDays = 30 }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Theory]
    [InlineData("Ok", 0, "retentionDays")]
    [InlineData("Ok", 3651, "retentionDays")]
    [InlineData("   ", 30, "name")]
    public async Task RetentionOutOfRange_400(string name, int days, string key)
    {
        using var client = _factory.CreateClient(UserRole.Admin);
        var resp = await client.PostAsJsonAsync("/api/orgs", new { name, retentionDays = days }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(body.GetProperty("errors").TryGetProperty(key, out _));
    }

    [Fact]
    public async Task DevHeaders_IgnoredInProduction()
    {
        using var prod = new MaldexAppFactory(sql, environment: "Production", authMode: "Dev");
        var ex = Assert.ThrowsAny<Exception>(() => prod.CreateClient());
        var inner = ex is InvalidOperationException ? ex : ex.InnerException ?? ex;
        Assert.IsType<InvalidOperationException>(inner);
        Assert.Equal("Dev auth is not allowed in Production", inner.Message);
        await Task.CompletedTask;
    }

    [Fact]
    public void DevAuth_RejectedInStaging()
    {
        using var staging = new MaldexAppFactory(sql, environment: "Staging", authMode: "Dev");
        var ex = Assert.ThrowsAny<Exception>(() => staging.CreateClient());
        var inner = ex is InvalidOperationException ? ex : ex.InnerException ?? ex;
        Assert.IsType<InvalidOperationException>(inner);
        Assert.Equal("Dev auth is not allowed in Staging", inner.Message);
    }

    [Fact]
    public void InvalidDevRole_FailsStartup()
    {
        using var bad = new MaldexAppFactory(sql, devRole: "Superuser");
        var ex = Assert.ThrowsAny<Exception>(() => bad.CreateClient());
        var inner = ex is InvalidOperationException ? ex : ex.InnerException ?? ex;
        Assert.IsType<InvalidOperationException>(inner);
        Assert.Contains("DevRole", inner.Message);
    }

    [Fact]
    public async Task ResponsesCarryCsp()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/me", Ct);
        Assert.Equal(
            "default-src 'self'; connect-src 'self' https://login.microsoftonline.com; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'",
            resp.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", resp.Headers.GetValues("Referrer-Policy").Single());
    }
}

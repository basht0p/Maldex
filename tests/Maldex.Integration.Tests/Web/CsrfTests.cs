using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

/// <summary>The CSRF header and Origin checks on /api and /hubs.</summary>
[Collection("sql")]
public sealed class CsrfTests(SqlServerFixture sql) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly MaldexAppFactory _factory = new(sql, authMode: "Local");

    public void Dispose() => _factory.Dispose();

    private HttpClient ClientWithoutCsrf()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove(MaldexAppFactory.CsrfHeader);
        return client;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage resp, string error)
    {
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(error, (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Mutation_without_csrf_header_is_403()
    {
        using var client = ClientWithoutCsrf();
        await AssertErrorAsync(await client.PostAsync("/api/auth/logout", null, Ct), "csrf");
        await AssertErrorAsync(await client.PostAsJsonAsync("/api/auth/login", new { userName = "a", password = "b" }, Ct), "csrf");
        await AssertErrorAsync(await client.DeleteAsync($"/api/admin/media/{Guid.NewGuid()}", Ct), "csrf");
        await AssertErrorAsync(await client.PutAsync("/api/admin/settings", JsonContent.Create(new { }), Ct), "csrf");
        await AssertErrorAsync(await client.PatchAsync($"/api/admin/users/{Guid.NewGuid()}", JsonContent.Create(new { }), Ct), "csrf");
        await AssertErrorAsync(await client.PostAsync("/hubs/tasks/negotiate?negotiateVersion=1", null, Ct), "csrf");

        // Any other value is refused too.
        client.DefaultRequestHeaders.Add(MaldexAppFactory.CsrfHeader, "0");
        await AssertErrorAsync(await client.PostAsync("/api/auth/logout", null, Ct), "csrf");
    }

    [Fact]
    public async Task Csrf_applies_in_dev_mode_too()
    {
        using var dev = new MaldexAppFactory(sql);
        using var client = dev.CreateClient(UserRole.Admin, "admin");
        client.DefaultRequestHeaders.Remove(MaldexAppFactory.CsrfHeader);
        await AssertErrorAsync(await client.PostAsJsonAsync("/api/orgs", new { name = "Acme", retentionDays = 30 }, Ct), "csrf");
    }

    [Fact]
    public async Task Mutation_with_foreign_origin_is_403()
    {
        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        req.Headers.Add("Origin", "https://evil.example");
        await AssertErrorAsync(await client.SendAsync(req, Ct), "origin");

        // A foreign Origin is refused on reads under /api as well, and a malformed one is foreign.
        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/auth/config");
        get.Headers.Add("Origin", "http://localhost:5173");
        await AssertErrorAsync(await client.SendAsync(get, Ct), "origin");
        using var bad = new HttpRequestMessage(HttpMethod.Get, "/api/auth/config");
        bad.Headers.TryAddWithoutValidation("Origin", "null");
        await AssertErrorAsync(await client.SendAsync(bad, Ct), "origin");
    }

    [Fact]
    public async Task Get_without_header_is_allowed()
    {
        using var client = ClientWithoutCsrf();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/config", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Same_origin_with_header_is_allowed()
    {
        using var client = _factory.CreateClient();
        foreach (var origin in new[] { "http://localhost", "http://localhost:80", "HTTP://LOCALHOST" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
            req.Headers.Add("Origin", origin);
            var resp = await client.SendAsync(req, Ct);
            Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.True(resp.IsSuccessStatusCode, origin);
        }
    }

    /// <summary>Sets the connection's peer address from <c>X-Test-Peer</c> before the app's middleware, as a real socket would.</summary>
    private sealed class PeerFactory(SqlServerFixture sql) : MaldexAppFactory(sql, authMode: "Local")
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s => s.AddSingleton<IStartupFilter, PeerFilter>());
        }

        private sealed class PeerFilter : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use((ctx, nxt) =>
                {
                    if (IPAddress.TryParse(ctx.Request.Headers["X-Test-Peer"].ToString(), out var ip)) ctx.Connection.RemoteIpAddress = ip;
                    return nxt(ctx);
                });
                next(app);
            };
        }
    }

    private static HttpRequestMessage ProxiedLogout(string peer, string origin)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        req.Headers.Add("X-Test-Peer", peer);
        req.Headers.Add("Origin", origin);
        req.Headers.Add("X-Forwarded-Proto", "https");
        req.Headers.Add("X-Forwarded-Host", "maldex.example");
        return req;
    }

    [Fact]
    public async Task Forwarded_proto_and_host_are_honoured_from_loopback_only()
    {
        using var f = new PeerFactory(sql);
        using var client = f.CreateClient();

        // A TLS proxy on this host: the public origin is https://maldex.example.
        using (var req = ProxiedLogout("127.0.0.1", "https://maldex.example"))
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(req, Ct)).StatusCode);
        using (var req = ProxiedLogout("::1", "https://maldex.example"))
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(req, Ct)).StatusCode);
        using (var req = ProxiedLogout("127.0.0.1", "https://evil.example"))
            await AssertErrorAsync(await client.SendAsync(req, Ct), "origin");

        // Anyone else's X-Forwarded-* is ignored: the request stays http://localhost, so that Origin is foreign.
        using (var req = ProxiedLogout("192.0.2.10", "https://maldex.example"))
            await AssertErrorAsync(await client.SendAsync(req, Ct), "origin");
    }

    [Fact]
    public async Task Session_cookie_is_host_prefixed_and_secure_over_https()
    {
        using var f = new PeerFactory(sql);
        await LocalAuth.SeedUserAsync(f, "alice", "correct horse battery", UserRole.Analyst);
        using var client = f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });

        using var https = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { userName = "alice", password = "correct horse battery" }) };
        https.Headers.Add("X-Test-Peer", "127.0.0.1");
        https.Headers.Add("X-Forwarded-Proto", "https");
        var secure = Assert.Single((await client.SendAsync(https, Ct)).Headers.GetValues("Set-Cookie"), c => c.Contains("maldex="));
        Assert.StartsWith("__Host-maldex=", secure);
        Assert.Contains("; secure", secure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; path=/", secure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=lax", secure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", secure, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", secure, StringComparison.OrdinalIgnoreCase);

        var plain = Assert.Single((await client.PostAsJsonAsync("/api/auth/login", new { userName = "alice", password = "correct horse battery" }, Ct)).Headers.GetValues("Set-Cookie"),
            c => c.Contains("maldex="));
        Assert.StartsWith("maldex=", plain);
        Assert.DoesNotContain("; secure", plain, StringComparison.OrdinalIgnoreCase);
    }
}

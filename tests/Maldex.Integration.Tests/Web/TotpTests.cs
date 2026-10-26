using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Web.Auth;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.Web;

/// <summary>TOTP enrolment, the TOTP sign-in step, hashed recovery codes, removal and regeneration.</summary>
[Collection("sql")]
public sealed class TotpTests(SqlServerFixture sql) : IDisposable
{
    private const string Password = "correct horse battery";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private MaldexAppFactory Factory(Dictionary<string, string?>? settings = null)
    {
        var f = new MaldexAppFactory(sql, authMode: "Local", settings: settings);
        _disposables.Add(f);
        return f;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage resp) => await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task AssertErrorAsync(HttpResponseMessage resp, HttpStatusCode status, string error)
    {
        Assert.Equal(status, resp.StatusCode);
        Assert.Equal(error, (await JsonAsync(resp)).GetProperty("error").GetString());
    }

    private static async Task AssertNextAsync(HttpResponseMessage resp, string next)
    {
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(next, (await JsonAsync(resp)).GetProperty("next").GetString());
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient c, string user = "alice") =>
        c.PostAsJsonAsync("/api/auth/login", new { userName = user, password = Password }, Ct);

    private static Task<HttpResponseMessage> TotpLoginAsync(HttpClient c, string code) =>
        c.PostAsJsonAsync("/api/auth/login/totp", new { code }, Ct);

    private static HttpRequestMessage DeleteTotp(string password) =>
        new(HttpMethod.Delete, "/api/auth/totp") { Content = JsonContent.Create(new { password }) };

    private static string WrongCode(string key)
    {
        var valid = new[] { -30, 0, 30 }.Select(d => Totp.Code(key, DateTimeOffset.UtcNow.AddSeconds(d))).ToHashSet();
        return new[] { "000000", "111111", "222222" }.First(c => !valid.Contains(c));
    }

    /// <summary>Runs setup and confirm on a signed-in client; returns the key and the 10 recovery codes.</summary>
    private static async Task<(string key, string[] codes)> EnrolAsync(HttpClient c)
    {
        var setup = await c.PostAsync("/api/auth/totp/setup", null, Ct);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var body = await JsonAsync(setup);
        var key = body.GetProperty("sharedKey").GetString()!;
        Assert.StartsWith("otpauth://totp/Maldex:", body.GetProperty("otpauthUri").GetString());
        Assert.Contains("secret=" + key, body.GetProperty("otpauthUri").GetString());
        var confirm = await c.PostAsJsonAsync("/api/auth/totp/confirm", new { code = Totp.Code(key, DateTimeOffset.UtcNow) }, Ct);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var result = await JsonAsync(confirm);
        var codes = result.GetProperty("recoveryCodes").EnumerateArray().Select(e => e.GetString()!).ToArray();
        return (key, codes);
    }

    private static async Task SetRequireTotpAsync(MaldexAppFactory f)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var s = await db.SystemSettings.FindAsync([1], Ct);
        if (s is null) db.SystemSettings.Add(new SystemSettings { RequireTotpForLocal = true });
        else s.RequireTotpForLocal = true;
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Enrol_then_sign_in_requires_code()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        var (key, codes) = await EnrolAsync(client);
        Assert.Equal(10, codes.Length);
        Assert.All(codes, c => Assert.Matches("^[a-z0-9]{5}-[a-z0-9]{5}$", c));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null, Ct)).StatusCode);
        await AssertNextAsync(await LoginAsync(client), "totp");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tasks", Ct)).StatusCode);
        await AssertNextAsync(await TotpLoginAsync(client, Totp.Code(key, DateTimeOffset.UtcNow)), "done");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tasks", Ct)).StatusCode);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me", Ct);
        Assert.True(me.GetProperty("totpEnabled").GetBoolean());
    }

    [Fact]
    public async Task Wrong_code_is_refused()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        var (key, _) = await EnrolAsync(client);
        await client.PostAsync("/api/auth/logout", null, Ct);
        await AssertNextAsync(await LoginAsync(client), "totp");
        await AssertErrorAsync(await TotpLoginAsync(client, WrongCode(key)), HttpStatusCode.Unauthorized, "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tasks", Ct)).StatusCode);
    }

    [Fact]
    public async Task Totp_login_without_a_partial_session_is_refused()
    {
        var f = Factory();
        using var client = f.CreateClient();
        await AssertErrorAsync(await TotpLoginAsync(client, "123456"), HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Recovery_code_works_once()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        var (_, codes) = await EnrolAsync(client);
        await client.PostAsync("/api/auth/logout", null, Ct);

        await AssertNextAsync(await LoginAsync(client), "totp");
        await AssertNextAsync(await TotpLoginAsync(client, codes[0]), "done");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tasks", Ct)).StatusCode);
        await client.PostAsync("/api/auth/logout", null, Ct);

        await AssertNextAsync(await LoginAsync(client), "totp");
        await AssertErrorAsync(await TotpLoginAsync(client, codes[0]), HttpStatusCode.Unauthorized, "invalid");
        // Accepted without the dash and in upper case; another code still works.
        await AssertNextAsync(await TotpLoginAsync(client, codes[1].Replace("-", "").ToUpperInvariant()), "done");
    }

    [Fact]
    public async Task Recovery_codes_are_hashed_at_rest()
    {
        var f = Factory();
        var alice = await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        var (_, codes) = await EnrolAsync(client);

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var tokens = await db.UserTokens.Where(t => t.UserId == alice.Id).ToListAsync(Ct);
        var stored = tokens.Single(t => t.Name == "RecoveryCodes").Value!;
        foreach (var code in codes)
        {
            Assert.DoesNotContain(code, stored);
            Assert.DoesNotContain(code.Replace("-", ""), stored);
        }
        var parts = stored.Split(';');
        Assert.Equal(10, parts.Length);
        Assert.All(parts, p => Assert.Matches("^[0-9a-f]{64}$", p));
    }

    [Fact]
    public async Task Required_totp_forces_enrolment()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        await SetRequireTotpAsync(f);
        using var client = f.CreateClient();

        await AssertNextAsync(await LoginAsync(client), "enrol-totp");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tasks", Ct)).StatusCode);

        var (_, codes) = await EnrolAsync(client);
        Assert.Equal(10, codes.Length);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tasks", Ct)).StatusCode);
    }

    [Fact]
    public async Task Confirm_during_enrolment_returns_next_done()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        await SetRequireTotpAsync(f);
        using var client = f.CreateClient();
        await AssertNextAsync(await LoginAsync(client), "enrol-totp");
        var key = (await JsonAsync(await client.PostAsync("/api/auth/totp/setup", null, Ct))).GetProperty("sharedKey").GetString()!;
        var confirm = await client.PostAsJsonAsync("/api/auth/totp/confirm", new { code = Totp.Code(key, DateTimeOffset.UtcNow) }, Ct);
        Assert.Equal("done", (await JsonAsync(confirm)).GetProperty("next").GetString());
    }

    [Fact]
    public async Task Confirm_with_a_wrong_code_is_refused()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        var key = (await JsonAsync(await client.PostAsync("/api/auth/totp/setup", null, Ct))).GetProperty("sharedKey").GetString()!;
        await AssertErrorAsync(await client.PostAsJsonAsync("/api/auth/totp/confirm", new { code = WrongCode(key) }, Ct), HttpStatusCode.BadRequest, "invalid");
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me", Ct);
        Assert.False(me.GetProperty("totpEnabled").GetBoolean());
    }

    [Fact]
    public async Task Break_glass_without_totp_must_enrol_when_password_login_off()
    {
        var f = Factory(new()
        {
            ["Maldex:Auth:PasswordLogin"] = "false",
            ["Maldex:Auth:Oidc:Enabled"] = "true",
            ["Maldex:Auth:Oidc:Authority"] = "https://idp.example.invalid",
            ["Maldex:Auth:Oidc:ClientId"] = "test-client",
        });
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Admin, u => u.IsBreakGlass = true);
        using var client = f.CreateClient();
        await AssertNextAsync(await LoginAsync(client), "enrol-totp");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tasks", Ct)).StatusCode);
    }

    [Fact]
    public async Task Cannot_remove_totp_when_required()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        await EnrolAsync(client);
        await SetRequireTotpAsync(f);

        await AssertErrorAsync(await client.SendAsync(DeleteTotp(Password), Ct), HttpStatusCode.Conflict, "totp_required");
    }

    [Fact]
    public async Task Remove_totp_needs_password_and_is_audited()
    {
        var f = Factory();
        var alice = await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        await EnrolAsync(client);

        await AssertErrorAsync(await client.SendAsync(DeleteTotp("wrong password here"), Ct), HttpStatusCode.BadRequest, "invalid");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(DeleteTotp(Password), Ct)).StatusCode);

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var audit = await db.AuditLogs.ToListAsync(Ct);
        Assert.Contains(audit, a => a.Action == "auth.totp_disabled" && a.UserId == alice.Id);
        Assert.Contains(audit, a => a.Action == "auth.totp_enabled" && a.UserId == alice.Id);

        // Back to password-only sign-in, and the recovery codes are gone.
        using var fresh = f.CreateClient();
        await AssertNextAsync(await LoginAsync(fresh), "done");
        Assert.Empty(await db.UserTokens.Where(t => t.UserId == alice.Id && t.Name == "RecoveryCodes").ToListAsync(Ct));
    }

    [Fact]
    public async Task Regenerating_codes_invalidates_old_ones()
    {
        var f = Factory();
        var alice = await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        var (_, oldCodes) = await EnrolAsync(client);

        await AssertErrorAsync(await client.PostAsJsonAsync("/api/auth/totp/recovery-codes", new { password = "wrong password here" }, Ct), HttpStatusCode.BadRequest, "invalid");
        var resp = await client.PostAsJsonAsync("/api/auth/totp/recovery-codes", new { password = Password }, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var newCodes = (await JsonAsync(resp)).GetProperty("recoveryCodes").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(10, newCodes.Length);

        using var second = f.CreateClient();
        await AssertNextAsync(await LoginAsync(second), "totp");
        await AssertErrorAsync(await TotpLoginAsync(second, oldCodes[0]), HttpStatusCode.Unauthorized, "invalid");
        await AssertNextAsync(await TotpLoginAsync(second, newCodes[0]), "done");

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        Assert.Contains(await db.AuditLogs.ToListAsync(Ct), a => a.Action == "auth.recovery_codes" && a.UserId == alice.Id);
    }

    [Fact]
    public void Partial_cookie_expires_after_10_minutes()
    {
        var f = Factory();
        using var _ = f.CreateClient();
        var o = f.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthSchemes.Partial);
        Assert.Equal(TimeSpan.FromMinutes(10), o.ExpireTimeSpan);
        Assert.False(o.SlidingExpiration);
    }
}

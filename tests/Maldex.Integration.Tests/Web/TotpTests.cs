using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Web.Auth;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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
        // The previous step's code (still inside the window): confirming records its step as used, so a sign-in right after
        // can use the current code.
        var confirm = await c.PostAsJsonAsync("/api/auth/totp/confirm", new { code = Totp.Code(key, DateTimeOffset.UtcNow.AddSeconds(-30)) }, Ct);
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

    private sealed class ClockFactory(SqlServerFixture sql, FakeTimeProvider clock) : MaldexAppFactory(sql, authMode: "Local")
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        }
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient c, string url, object body) => c.PostAsJsonAsync(url, body, Ct);

    [Fact]
    public async Task Reissued_session_cookies_keep_the_seven_day_cap()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var f = new ClockFactory(sql, clock);
        _disposables.Add(f);
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);

        // Almost six days in (activity every 11 hours keeps the sliding expiry alive).
        for (var i = 0; i < 13; i++)
        {
            clock.Advance(TimeSpan.FromHours(11));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
        }

        // Setup, confirm, regenerate and remove each re-issue the cookie; none may restart the 7-day cap.
        var key = (await JsonAsync(await client.PostAsync("/api/auth/totp/setup", null, Ct))).GetProperty("sharedKey").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/api/auth/totp/confirm", new { code = Totp.Code(key, clock.GetUtcNow()) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/api/auth/totp/recovery-codes", new { password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(DeleteTotp(Password), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);

        clock.Advance(TimeSpan.FromHours(11));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
        clock.Advance(TimeSpan.FromHours(11));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
        clock.Advance(TimeSpan.FromHours(4));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Setup_keeps_the_original_partial_window()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var f = new ClockFactory(sql, clock);
        _disposables.Add(f);
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        await SetRequireTotpAsync(f);
        using var client = f.CreateClient();
        await AssertNextAsync(await LoginAsync(client), "enrol-totp");

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/totp/setup", null, Ct)).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(5));
        // 11 minutes after sign-in: had setup extended the window, this would still be allowed.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/totp/setup", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Partial_sessions_at_other_steps_cannot_enrol_or_manage_totp()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst, u => u.MustChangePassword = true);
        using var client = f.CreateClient();
        await AssertNextAsync(await LoginAsync(client), "change-password");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/totp/setup", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, "/api/auth/totp/confirm", new { code = "123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tasks", Ct)).StatusCode);

        // A partial session still owing the TOTP code can neither re-key, regenerate nor remove.
        await LocalAuth.SeedUserAsync(f, "bob", Password, UserRole.Analyst);
        using var bob = await LocalAuth.SignInAsync(f, "bob", Password);
        await EnrolAsync(bob);
        await bob.PostAsync("/api/auth/logout", null, Ct);
        await AssertNextAsync(await LoginAsync(bob, "bob"), "totp");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.PostAsync("/api/auth/totp/setup", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(bob, "/api/auth/totp/confirm", new { code = "123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(bob, "/api/auth/totp/recovery-codes", new { password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.SendAsync(DeleteTotp(Password), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/api/tasks", Ct)).StatusCode);
    }

    [Fact]
    public async Task Totp_login_is_rate_limited()
    {
        var f = Factory(new() { ["Maldex:Auth:LoginRateLimitPerMinute"] = "10" });
        using var client = f.CreateClient();
        for (var i = 0; i < 10; i++)
            await AssertErrorAsync(await TotpLoginAsync(client, "123456"), HttpStatusCode.Unauthorized, "unauthenticated");
        await AssertErrorAsync(await TotpLoginAsync(client, "123456"), HttpStatusCode.TooManyRequests, "rate_limited");
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_account()
    {
        var f = Factory();
        var alice = await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        var (key, _) = await EnrolAsync(client);
        await client.PostAsync("/api/auth/logout", null, Ct);
        await AssertNextAsync(await LoginAsync(client), "totp");

        for (var i = 0; i < 5; i++)
            await AssertErrorAsync(await TotpLoginAsync(client, WrongCode(key)), HttpStatusCode.Unauthorized, "invalid");
        // Even the right code is refused now.
        await AssertErrorAsync(await TotpLoginAsync(client, Totp.Code(key, DateTimeOffset.UtcNow)), HttpStatusCode.Unauthorized, "locked");

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        Assert.Contains(await db.AuditLogs.ToListAsync(Ct), a => a.Action == "auth.lockout" && a.UserId == alice.Id);
    }

    [Fact]
    public async Task Fresh_password_logins_do_not_reset_the_totp_failure_count()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        var (key, _) = await EnrolAsync(client);
        await client.PostAsync("/api/auth/logout", null, Ct);
        await AssertNextAsync(await LoginAsync(client), "totp");

        for (var i = 0; i < 3; i++)
            await AssertErrorAsync(await TotpLoginAsync(client, WrongCode(key)), HttpStatusCode.Unauthorized, "invalid");
        await AssertNextAsync(await LoginAsync(client), "totp");
        for (var i = 0; i < 2; i++)
            await AssertErrorAsync(await TotpLoginAsync(client, WrongCode(key)), HttpStatusCode.Unauthorized, "invalid");
        await AssertErrorAsync(await TotpLoginAsync(client, Totp.Code(key, DateTimeOffset.UtcNow)), HttpStatusCode.Unauthorized, "locked");
    }

    [Fact]
    public async Task A_totp_code_cannot_be_used_twice()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        var (key, _) = await EnrolAsync(client);
        await client.PostAsync("/api/auth/logout", null, Ct);

        var code = Totp.Code(key, DateTimeOffset.UtcNow);
        await AssertNextAsync(await LoginAsync(client), "totp");
        await AssertNextAsync(await TotpLoginAsync(client, code), "done");
        await client.PostAsync("/api/auth/logout", null, Ct);
        await AssertNextAsync(await LoginAsync(client), "totp");
        await AssertErrorAsync(await TotpLoginAsync(client, code), HttpStatusCode.Unauthorized, "invalid");
    }

    [Fact]
    public async Task Setup_and_confirm_are_refused_when_totp_is_already_on()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        var (key, _) = await EnrolAsync(client);

        await AssertErrorAsync(await client.PostAsync("/api/auth/totp/setup", null, Ct), HttpStatusCode.Conflict, "totp_enabled");
        await AssertErrorAsync(await PostAsync(client, "/api/auth/totp/confirm", new { code = Totp.Code(key, DateTimeOffset.UtcNow) }), HttpStatusCode.Conflict, "totp_enabled");
    }

    [Fact]
    public async Task Remove_without_totp_is_refused()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        await AssertErrorAsync(await client.SendAsync(DeleteTotp(Password), Ct), HttpStatusCode.Conflict, "totp_not_enabled");
    }

    [Fact]
    public async Task A_correct_password_resets_the_failure_count_on_regenerate()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        await EnrolAsync(client);

        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < 4; i++)
                await AssertErrorAsync(await PostAsync(client, "/api/auth/totp/recovery-codes", new { password = "wrong password here" }), HttpStatusCode.BadRequest, "invalid");
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/api/auth/totp/recovery-codes", new { password = Password })).StatusCode);
        }
    }

    /// <summary>RFC 6238 Appendix B (SHA-1 seed "12345678901234567890"), truncated to the 6 digits Maldex uses.</summary>
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Codes_match_the_rfc_6238_test_vectors(long unixTime, string code) =>
        Assert.Equal(code, TotpCode.For(System.Text.Encoding.ASCII.GetBytes("12345678901234567890"), unixTime / 30));

    [Fact]
    public void Base32_decoding_matches_the_rfc_4648_vectors()
    {
        Assert.Equal("foobar"u8.ToArray(), TotpCode.Base32Decode("MZXW6YTBOI======"));
        Assert.Equal("foobar"u8.ToArray(), TotpCode.Base32Decode("mzxw6ytboi"));
    }

    [Fact]
    public async Task The_code_that_confirmed_enrolment_cannot_sign_in()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        var setup = await JsonAsync(await client.PostAsync("/api/auth/totp/setup", null, Ct));
        var key = setup.GetProperty("sharedKey").GetString()!;
        var code = Totp.Code(key, DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/totp/confirm", new { code }, Ct)).StatusCode);
        await client.PostAsync("/api/auth/logout", null, Ct);

        await AssertNextAsync(await LoginAsync(client), "totp");
        await AssertErrorAsync(await TotpLoginAsync(client, code), HttpStatusCode.Unauthorized, "invalid");
    }

    /// <summary>
    /// Arms on demand; then, just before the save that records a TOTP step for the first time, inserts that row from another
    /// connection, as a concurrent sign-in with the same code would. The save then hits the primary key.
    /// </summary>
    private sealed class FirstStepRace(string connectionString) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var token = eventData.Context!.ChangeTracker.Entries<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>()
                .FirstOrDefault(e => e.State == EntityState.Added && e.Entity.LoginProvider == "maldex" && e.Entity.Name == "TotpLastStep");
            if (Armed && token is not null)
            {
                Armed = false;
                Fired = true;
                await using var conn = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
                await conn.OpenAsync(cancellationToken);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO UserTokens (UserId, LoginProvider, Name, Value) VALUES (@u, 'maldex', 'TotpLastStep', @v)";
                cmd.Parameters.AddWithValue("@u", token.Entity.UserId);
                cmd.Parameters.AddWithValue("@v", token.Entity.Value ?? "");
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Losing_the_race_to_record_the_first_step_is_invalid_not_500()
    {
        var f = Factory();
        var race = new FirstStepRace(f.ConnectionString);
        using var host = f.WithWebHostBuilder(b => b.ConfigureServices(s => s.ConfigureDbContext<MaldexDbContext>(o => o.AddInterceptors(race))));
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = host.CreateClient();
        await AssertNextAsync(await LoginAsync(client), "done");
        var key = (await JsonAsync(await client.PostAsync("/api/auth/totp/setup", null, Ct))).GetProperty("sharedKey").GetString()!;
        // Enrol with the code of the previous step, so the next sign-in can use the current one.
        var confirm = await client.PostAsJsonAsync("/api/auth/totp/confirm", new { code = Totp.Code(key, DateTimeOffset.UtcNow.AddSeconds(-30)) }, Ct);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        await client.PostAsync("/api/auth/logout", null, Ct);
        // Start over without a recorded step: the race is about the very first one.
        await using (var scope = f.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MaldexDbContext>().Database
                .ExecuteSqlRawAsync("DELETE FROM UserTokens WHERE LoginProvider = 'maldex' AND Name = 'TotpLastStep'", Ct);

        await AssertNextAsync(await LoginAsync(client), "totp");
        race.Armed = true;
        await AssertErrorAsync(await TotpLoginAsync(client, Totp.Code(key, DateTimeOffset.UtcNow)), HttpStatusCode.Unauthorized, "invalid");
        Assert.True(race.Fired);
    }

    /// <summary>Makes the next save of a modified user fail as a concurrency conflict (UserManager then reports failure), once.</summary>
    private sealed class FailNextUserSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<AppUser>().Any(e => e.State == EntityState.Modified))
            {
                Armed = false;
                throw new DbUpdateConcurrencyException("simulated conflict");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task A_failed_key_reset_at_setup_changes_nothing()
    {
        var f = Factory();
        var fail = new FailNextUserSave();
        using var host = f.WithWebHostBuilder(b => b.ConfigureServices(s => s.ConfigureDbContext<MaldexDbContext>(o => o.AddInterceptors(fail))));
        var alice = await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = host.CreateClient();
        await AssertNextAsync(await LoginAsync(client), "done");
        var key = (await JsonAsync(await client.PostAsync("/api/auth/totp/setup", null, Ct))).GetProperty("sharedKey").GetString()!;
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>();
            await users.SetAuthenticationTokenAsync((await users.FindByIdAsync(alice.Id.ToString()))!, "maldex", "TotpLastStep", "12345");
        }

        fail.Armed = true;
        await AssertErrorAsync(await client.PostAsync("/api/auth/totp/setup", null, Ct), HttpStatusCode.BadRequest, "invalid");

        await using var check = f.Services.CreateAsyncScope();
        var um = check.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>();
        var reloaded = (await um.FindByIdAsync(alice.Id.ToString()))!;
        Assert.Equal(key, await um.GetAuthenticatorKeyAsync(reloaded));
        Assert.Equal("12345", await um.GetAuthenticationTokenAsync(reloaded, "maldex", "TotpLastStep"));
        // The session is untouched: the stamp did not change.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
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

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Maldex.Integration.Tests.Web;

/// <summary>Password sign-in in <c>Maldex:Auth:Mode=Local</c>: cookies, failures, lockout, forced password change, revocation.</summary>
[Collection("sql")]
public sealed class LocalLoginTests(SqlServerFixture sql) : IDisposable
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

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { userName, password }, Ct);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);

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

    private static async Task UpdateUserAsync(MaldexAppFactory f, Guid id, Action<AppUser> change)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == id, Ct);
        change(user);
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Login_succeeds_and_me_returns_the_user()
    {
        var f = Factory();
        var alice = await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = f.CreateClient();

        await AssertNextAsync(await LoginAsync(client, "alice", Password), "done");

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me", Ct);
        Assert.Equal(alice.Id, me.GetProperty("id").GetGuid());
        Assert.Equal("alice", me.GetProperty("displayName").GetString());
        Assert.Equal("Analyst", me.GetProperty("role").GetString());
        Assert.Equal("Local", me.GetProperty("kind").GetString());
        Assert.False(me.GetProperty("totpEnabled").GetBoolean());
        Assert.False(me.GetProperty("isBreakGlass").GetBoolean());
        Assert.False(me.GetProperty("roleManagedByIdp").GetBoolean());
        Assert.True(me.GetProperty("maxUploadBytes").GetInt64() > 0);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tasks", Ct)).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_api_returns_401_without_redirect()
    {
        var f = Factory();
        using var client = f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var resp = await client.GetAsync("/api/tasks", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Null(resp.Headers.Location);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_both_return_invalid()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = f.CreateClient();

        await AssertErrorAsync(await LoginAsync(client, "alice", "wrong password here"), HttpStatusCode.Unauthorized, "invalid");
        await AssertErrorAsync(await LoginAsync(client, "nobody", Password), HttpStatusCode.Unauthorized, "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Login_trims_and_ignores_case_of_user_name()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = f.CreateClient();

        await AssertNextAsync(await LoginAsync(client, " ALICE ", Password), "done");
        Assert.Equal("alice", (await client.GetFromJsonAsync<JsonElement>("/api/me", Ct)).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Password_with_spaces_and_unicode_is_not_trimmed()
    {
        const string exact = "  pässwörd with spaces  ";
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", exact, UserRole.Analyst);
        using var client = f.CreateClient();

        await AssertErrorAsync(await LoginAsync(client, "alice", exact.Trim()), HttpStatusCode.Unauthorized, "invalid");
        await AssertNextAsync(await LoginAsync(client, "alice", exact), "done");
    }

    [Fact]
    public async Task Five_failures_lock_the_account()
    {
        var f = Factory();
        var alice = await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = f.CreateClient();

        for (var i = 0; i < 5; i++)
            await AssertErrorAsync(await LoginAsync(client, "alice", "wrong password " + i), HttpStatusCode.Unauthorized, "invalid");
        await AssertErrorAsync(await LoginAsync(client, "alice", Password), HttpStatusCode.Unauthorized, "locked");
        // A wrong password while locked does not reveal the lock.
        await AssertErrorAsync(await LoginAsync(client, "alice", "wrong password x"), HttpStatusCode.Unauthorized, "invalid");

        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            var row = await db.Users.SingleAsync(u => u.Id == alice.Id, Ct);
            Assert.True(row.LockoutEnd > DateTimeOffset.UtcNow.AddMinutes(14));
            Assert.True(row.LockoutEnd < DateTimeOffset.UtcNow.AddMinutes(16));
            Assert.Contains(await db.AuditLogs.ToListAsync(Ct), a => a.Action == "auth.lockout" && a.UserId == alice.Id);
        }

        await UpdateUserAsync(f, alice.Id, u => u.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(-1));
        await AssertNextAsync(await LoginAsync(client, "alice", Password), "done");
    }

    [Fact]
    public async Task Disabled_user_cannot_sign_in()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst, u => u.IsDisabled = true);
        using var client = f.CreateClient();

        await AssertErrorAsync(await LoginAsync(client, "alice", "wrong password here"), HttpStatusCode.Unauthorized, "invalid");
        await AssertErrorAsync(await LoginAsync(client, "alice", Password), HttpStatusCode.Unauthorized, "disabled");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Password_login_off_allows_only_break_glass()
    {
        var f = Factory(new()
        {
            ["Maldex:Auth:PasswordLogin"] = "false",
            ["Maldex:Auth:Oidc:Enabled"] = "true",
            ["Maldex:Auth:Oidc:Authority"] = "https://idp.example.test/",
            ["Maldex:Auth:Oidc:ClientId"] = "maldex",
        });
        await LocalAuth.SeedUserAsync(f, "analyst", Password, UserRole.Analyst);
        await LocalAuth.SeedUserAsync(f, "admin", Password, UserRole.Admin);
        await LocalAuth.SeedUserAsync(f, "glass", Password, UserRole.Admin, u => u.IsBreakGlass = true);
        using var client = f.CreateClient();

        await AssertErrorAsync(await LoginAsync(client, "analyst", Password), HttpStatusCode.Unauthorized, "password_login_disabled");
        await AssertErrorAsync(await LoginAsync(client, "admin", Password), HttpStatusCode.Unauthorized, "password_login_disabled");
        await AssertErrorAsync(await LoginAsync(client, "analyst", "wrong password here"), HttpStatusCode.Unauthorized, "invalid");
        // Break-glass without TOTP must enrol before anything else.
        await AssertNextAsync(await LoginAsync(client, "glass", Password), "enrol-totp");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tasks", Ct)).StatusCode);

        var config = await client.GetFromJsonAsync<JsonElement>("/api/auth/config", Ct);
        Assert.False(config.GetProperty("passwordLogin").GetBoolean());
        Assert.Equal("Single sign-on", config.GetProperty("sso").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Disabled_user_is_rejected_on_next_request()
    {
        var f = Factory();
        var alice = await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);

        // The stamp is not touched: the next request must still be refused (no 5-minute wait).
        await UpdateUserAsync(f, alice.Id, u => u.IsDisabled = true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tasks", Ct)).StatusCode);
    }

    [Fact]
    public async Task Must_change_password_blocks_everything_else()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst, u => u.MustChangePassword = true);
        using var client = f.CreateClient();

        await AssertNextAsync(await LoginAsync(client, "alice", Password), "change-password");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tasks", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);

        var change = await client.PostAsJsonAsync("/api/auth/password", new { currentPassword = Password, newPassword = "a brand new passphrase" }, Ct);
        await AssertNextAsync(change, "done");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tasks", Ct)).StatusCode);

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var row = await db.Users.SingleAsync(u => u.UserName == "alice", Ct);
        Assert.False(row.MustChangePassword);
        Assert.Contains(await db.AuditLogs.ToListAsync(Ct), a => a.Action == "auth.password_changed" && a.UserId == row.Id);

        // The new password works, the old one no longer does.
        using var fresh = f.CreateClient();
        await AssertErrorAsync(await LoginAsync(fresh, "alice", Password), HttpStatusCode.Unauthorized, "invalid");
        await AssertNextAsync(await LoginAsync(fresh, "alice", "a brand new passphrase"), "done");
    }

    [Fact]
    public async Task Short_new_password_is_refused()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);

        var resp = await client.PostAsJsonAsync("/api/auth/password", new { currentPassword = Password, newPassword = "elevenchars" }, Ct);
        await AssertErrorAsync(resp, HttpStatusCode.BadRequest, "password_too_short");
        var tooLong = await client.PostAsJsonAsync("/api/auth/password", new { currentPassword = Password, newPassword = new string('x', 257) }, Ct);
        await AssertErrorAsync(tooLong, HttpStatusCode.BadRequest, "password_too_long");
    }

    [Fact]
    public async Task Password_change_requires_current_password()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);

        var resp = await client.PostAsJsonAsync("/api/auth/password", new { currentPassword = "not the password", newPassword = "a brand new passphrase" }, Ct);
        await AssertErrorAsync(resp, HttpStatusCode.BadRequest, "invalid");
        using var anonymous = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/auth/password", new { currentPassword = Password, newPassword = "a brand new passphrase" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Password_reset_by_stamp_ends_other_sessions()
    {
        var f = Factory(new() { ["Maldex:Auth:StampValidationInterval"] = "00:00:00" });
        var alice = await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
        // A stamp refresh keeps the session's own claims (the absolute cap still applies afterwards).
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);

        await using (var scope = f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await users.FindByIdAsync(alice.Id.ToString());
            await users.UpdateSecurityStampAsync(user!);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task User_without_role_gets_no_role_403_but_me_works()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "pending", Password, role: null);
        using var client = await LocalAuth.SignInAsync(f, "pending", Password);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me", Ct);
        Assert.Equal(JsonValueKind.Null, me.GetProperty("role").ValueKind);
        await AssertErrorAsync(await client.GetAsync("/api/tasks", Ct), HttpStatusCode.Forbidden, "no_role");
    }

    [Fact]
    public async Task Logout_clears_the_session()
    {
        var f = Factory();
        var alice = await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);

        Assert.True((await client.PostAsync("/api/auth/logout", null, Ct)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        Assert.Contains(await db.AuditLogs.ToListAsync(Ct), a => a.Action == "auth.logout" && a.UserId == alice.Id);
    }

    [Fact]
    public async Task Login_is_rate_limited()
    {
        var f = Factory(new() { ["Maldex:Auth:LoginRateLimitPerMinute"] = "10" });
        using var client = f.CreateClient();

        for (var i = 0; i < 10; i++)
            await AssertErrorAsync(await LoginAsync(client, "nobody", Password), HttpStatusCode.Unauthorized, "invalid");
        await AssertErrorAsync(await LoginAsync(client, "nobody", Password), HttpStatusCode.TooManyRequests, "rate_limited");
        // Only the sign-in endpoints are limited.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/config", Ct)).StatusCode);
    }

    [Fact]
    public async Task Config_reports_password_login_and_no_sso()
    {
        var f = Factory();
        using var client = f.CreateClient();

        var config = await client.GetFromJsonAsync<JsonElement>("/api/auth/config", Ct);
        Assert.True(config.GetProperty("passwordLogin").GetBoolean());
        Assert.Equal(JsonValueKind.Null, config.GetProperty("sso").ValueKind);
    }

    [Fact]
    public async Task Keys_dir_is_created_with_mode_0700()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);

        Assert.True(Directory.Exists(f.KeysDir));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(f.KeysDir));
        Assert.NotEmpty(Directory.GetFiles(f.KeysDir, "*.xml"));
    }

    private sealed class ClockFactory(SqlServerFixture sql, FakeTimeProvider clock) : MaldexAppFactory(sql, authMode: "Local")
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        }
    }

    [Fact]
    public async Task Session_slides_but_ends_seven_days_after_sign_in()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var f = new ClockFactory(sql, clock);
        _disposables.Add(f);
        await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "alice", Password);

        // Active every 6 hours: the 12-hour sliding expiry never runs out, until the 7-day cap does.
        for (var i = 0; i < 27; i++)
        {
            clock.Advance(TimeSpan.FromHours(6));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
        }
        clock.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);

        // Idle for longer than 12 hours ends a fresh session too.
        using var idle = await LocalAuth.SignInAsync(f, "alice", Password);
        clock.Advance(TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await idle.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Login_is_audited()
    {
        var f = Factory();
        var alice = await LocalAuth.SeedUserAsync(f, "alice", Password, UserRole.Analyst);
        using var client = f.CreateClient();
        await AssertNextAsync(await LoginAsync(client, "alice", Password), "done");
        await AssertErrorAsync(await LoginAsync(client, "nobody", Password), HttpStatusCode.Unauthorized, "invalid");
        await AssertErrorAsync(await LoginAsync(client, "alice", "wrong password here"), HttpStatusCode.Unauthorized, "invalid");

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var rows = await db.AuditLogs.ToListAsync(Ct);
        Assert.Contains(rows, a => a.Action == "auth.login" && a.UserId == alice.Id);
        Assert.Contains(rows, a => a.Action == "auth.login_failed" && a.UserId == null && a.Target == "nobody");
        Assert.Contains(rows, a => a.Action == "auth.login_failed" && a.UserId == alice.Id);
        Assert.NotNull((await db.Users.SingleAsync(u => u.Id == alice.Id, Ct)).LastSignInAt);
    }
}

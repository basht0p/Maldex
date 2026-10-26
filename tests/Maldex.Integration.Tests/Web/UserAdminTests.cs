using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

/// <summary>
/// With <c>PasswordLogin=false</c> only break-glass admins may sign in, and they must enrol TOTP first, so tests sign in on a host with
/// password login on, then run the same database and key ring (and so the same session cookie) with password login off.
/// </summary>
internal static class PasswordOffHost
{
    public static async Task<(WebApplicationFactory<Program> host, HttpClient client)> SignInAsync(MaldexAppFactory f, string userName, string password)
    {
        using var login = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var resp = await login.PostAsJsonAsync("/api/auth/login", new { userName, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var cookie = string.Join("; ", resp.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        var host = f.WithWebHostBuilder(b => b.UseSetting("Maldex:Auth:PasswordLogin", "false"));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return (host, client);
    }
}

/// <summary>The user administration API (<c>/api/admin/users</c>, <c>/api/admin/settings</c>) and its last-admin guard.</summary>
[Collection("sql")]
public sealed class UserAdminTests(SqlServerFixture sql) : IDisposable
{
    private const string Password = "correct horse battery";
    private const string OtherPassword = "another long passphrase";
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

    private static Dictionary<string, string?> SsoOnly() => new()
    {
        ["Maldex:Auth:PasswordLogin"] = "false",
        ["Maldex:Auth:Oidc:Enabled"] = "true",
        ["Maldex:Auth:Oidc:Authority"] = "https://idp.example.invalid",
        ["Maldex:Auth:Oidc:ClientId"] = "test-client",
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage resp) => await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task AssertErrorAsync(HttpResponseMessage resp, HttpStatusCode status, string error)
    {
        Assert.Equal(status, resp.StatusCode);
        Assert.Equal(error, (await JsonAsync(resp)).GetProperty("error").GetString());
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient c, Guid id, object body) =>
        c.PatchAsJsonAsync($"/api/admin/users/{id}", body, Ct);

    private static Task<HttpResponseMessage> CreateAsync(HttpClient c, string userName, string role = "Analyst", string password = Password, bool isBreakGlass = false) =>
        c.PostAsJsonAsync("/api/admin/users", new { userName, displayName = userName, role, password, mustChangePassword = true, isBreakGlass }, Ct);

    private async Task<(MaldexAppFactory f, AppUser admin, HttpClient client)> AdminAsync(Dictionary<string, string?>? settings = null)
    {
        var f = Factory(settings);
        var admin = await LocalAuth.SeedUserAsync(f, "root", Password, UserRole.Admin);
        var client = await LocalAuth.SignInAsync(f, "root", Password);
        _disposables.Add(client);
        return (f, admin, client);
    }

    private static async Task<string?> LoginNextAsync(MaldexAppFactory f, string userName, string password)
    {
        using var c = f.CreateClient();
        var resp = await c.PostAsJsonAsync("/api/auth/login", new { userName, password }, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await JsonAsync(resp)).GetProperty("next").GetString();
    }

    [Fact]
    public async Task Analyst_cannot_list_users()
    {
        var f = Factory();
        await LocalAuth.SeedUserAsync(f, "ann", Password, UserRole.Analyst);
        using var client = await LocalAuth.SignInAsync(f, "ann", Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/settings", Ct)).StatusCode);
    }

    [Fact]
    public async Task Create_lists_and_new_user_must_change_password()
    {
        var (f, _, admin) = await AdminAsync();
        var resp = await CreateAsync(admin, "bob");
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await JsonAsync(resp);
        Assert.Equal("bob", dto.GetProperty("userName").GetString());
        Assert.Equal("Local", dto.GetProperty("kind").GetString());
        Assert.Equal("Analyst", dto.GetProperty("role").GetString());
        Assert.False(dto.GetProperty("isDisabled").GetBoolean());

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users", Ct);
        Assert.Contains(list.EnumerateArray(), u => u.GetProperty("userName").GetString() == "bob");
        Assert.Equal("change-password", await LoginNextAsync(f, "bob", Password));

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        Assert.Contains(await db.AuditLogs.ToListAsync(Ct), a => a.Action == "user.create" && a.Target == "bob");
    }

    [Fact]
    public async Task Create_refuses_case_insensitive_duplicate()
    {
        var (_, _, admin) = await AdminAsync();
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(admin, "Bob")).StatusCode);
        await AssertErrorAsync(await CreateAsync(admin, "bob"), HttpStatusCode.Conflict, "duplicate_user_name");
        await AssertErrorAsync(await CreateAsync(admin, " ROOT "), HttpStatusCode.Conflict, "duplicate_user_name");
    }

    [Fact]
    public async Task Create_refuses_invalid_user_name_and_short_password()
    {
        var (_, _, admin) = await AdminAsync();
        await AssertErrorAsync(await CreateAsync(admin, "bad name!"), HttpStatusCode.BadRequest, "invalid_user_name");
        await AssertErrorAsync(await CreateAsync(admin, ""), HttpStatusCode.BadRequest, "invalid_user_name");
        await AssertErrorAsync(await CreateAsync(admin, new string('a', 65)), HttpStatusCode.BadRequest, "invalid_user_name");
        await AssertErrorAsync(await CreateAsync(admin, "shorty", password: "short"), HttpStatusCode.BadRequest, "password_too_short");
    }

    [Fact]
    public async Task Demoting_the_last_admin_is_refused()
    {
        var (f, root, admin) = await AdminAsync();
        await AssertErrorAsync(await PatchAsync(admin, root.Id, new { role = "Analyst" }), HttpStatusCode.Conflict, "last_admin");

        await LocalAuth.SeedUserAsync(f, "second", Password, UserRole.Admin);
        var ok = await PatchAsync(admin, root.Id, new { role = "Analyst" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Analyst", (await JsonAsync(ok)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Disabling_the_last_admin_is_refused()
    {
        var (f, root, admin) = await AdminAsync();
        await AssertErrorAsync(await PatchAsync(admin, root.Id, new { isDisabled = true }), HttpStatusCode.Conflict, "last_admin");

        // A disabled second admin does not count either.
        var second = await LocalAuth.SeedUserAsync(f, "second", Password, UserRole.Admin, u => u.IsDisabled = true);
        await AssertErrorAsync(await PatchAsync(admin, root.Id, new { isDisabled = true }), HttpStatusCode.Conflict, "last_admin");
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(admin, second.Id, new { isDisabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(admin, root.Id, new { isDisabled = true })).StatusCode);
    }

    [Fact]
    public async Task Last_admin_counts_only_admins_who_can_sign_in()
    {
        // Password login is off: a local admin without break-glass cannot sign in, so it does not count as "another admin".
        var settings = SsoOnly();
        settings["Maldex:Auth:PasswordLogin"] = "true";
        var f = Factory(settings);
        var glass = await LocalAuth.SeedUserAsync(f, "glass", Password, UserRole.Admin, u => u.IsBreakGlass = true);
        await LocalAuth.SeedUserAsync(f, "plain", Password, UserRole.Admin);
        var (host, client) = await PasswordOffHost.SignInAsync(f, "glass", Password);
        _disposables.Add(host);
        _disposables.Add(client);

        await AssertErrorAsync(await PatchAsync(client, glass.Id, new { role = "Analyst" }), HttpStatusCode.Conflict, "last_admin");
        await AssertErrorAsync(await PatchAsync(client, glass.Id, new { isBreakGlass = false }), HttpStatusCode.Conflict, "last_admin");

        // An SSO admin counts while SSO is enabled.
        await LocalAuth.SeedUserAsync(f, "sso-admin", Password, UserRole.Admin, u => u.Kind = UserKind.Sso);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(client, glass.Id, new { role = "Analyst" })).StatusCode);
    }

    [Fact]
    public async Task Sso_role_is_locked_when_mapping_configured()
    {
        var settings = SsoOnly();
        settings["Maldex:Auth:PasswordLogin"] = "true";
        settings["Maldex:Auth:Oidc:RoleClaim"] = "groups";
        settings["Maldex:Auth:Oidc:AdminValues:0"] = "maldex-admins";
        var (f, _, admin) = await AdminAsync(settings);
        var sso = await LocalAuth.SeedUserAsync(f, "sso-abcdef12", Password, UserRole.Analyst, u => u.Kind = UserKind.Sso);

        await AssertErrorAsync(await PatchAsync(admin, sso.Id, new { role = "Admin" }), HttpStatusCode.Conflict, "role_managed_by_idp");
        var ok = await PatchAsync(admin, sso.Id, new { isDisabled = true });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True((await JsonAsync(ok)).GetProperty("roleManagedByIdp").GetBoolean());
    }

    [Fact]
    public async Task Sso_role_is_assignable_without_mapping()
    {
        var settings = SsoOnly();
        settings["Maldex:Auth:PasswordLogin"] = "true";
        var (f, _, admin) = await AdminAsync(settings);
        var sso = await LocalAuth.SeedUserAsync(f, "sso-abcdef12", Password, role: null, u => u.Kind = UserKind.Sso);
        var ok = await PatchAsync(admin, sso.Id, new { role = "Analyst" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Analyst", (await JsonAsync(ok)).GetProperty("role").GetString());
        var none = await PatchAsync(admin, sso.Id, new { role = "none" });
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(none)).GetProperty("role").ValueKind);
    }

    [Fact]
    public async Task Break_glass_only_on_local_admins()
    {
        var (f, root, admin) = await AdminAsync();
        var analyst = await LocalAuth.SeedUserAsync(f, "ann", Password, UserRole.Analyst);
        await AssertErrorAsync(await PatchAsync(admin, analyst.Id, new { isBreakGlass = true }), HttpStatusCode.Conflict, "break_glass_requires_local_admin");
        await AssertErrorAsync(await CreateAsync(admin, "ann2", "Analyst", isBreakGlass: true), HttpStatusCode.Conflict, "break_glass_requires_local_admin");
        var sso = await LocalAuth.SeedUserAsync(f, "sso-1", Password, UserRole.Admin, u => u.Kind = UserKind.Sso);
        await AssertErrorAsync(await PatchAsync(admin, sso.Id, new { isBreakGlass = true }), HttpStatusCode.Conflict, "break_glass_requires_local_admin");

        var ok = await PatchAsync(admin, root.Id, new { isBreakGlass = true });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True((await JsonAsync(ok)).GetProperty("isBreakGlass").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(admin, "glass2", "Admin", isBreakGlass: true)).StatusCode);
    }

    [Fact]
    public async Task Update_unknown_user_is_not_found()
    {
        var (_, _, admin) = await AdminAsync();
        await AssertErrorAsync(await PatchAsync(admin, Guid.NewGuid(), new { displayName = "x" }), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Reset_password_clears_lockout_and_forces_change()
    {
        var (f, _, admin) = await AdminAsync();
        var bob = await LocalAuth.SeedUserAsync(f, "bob", Password, UserRole.Analyst);
        using (var c = f.CreateClient())
            for (var i = 0; i < 5; i++)
                await c.PostAsJsonAsync("/api/auth/login", new { userName = "bob", password = "wrong password " + i }, Ct);
        using (var c = f.CreateClient())
        {
            var locked = await c.PostAsJsonAsync("/api/auth/login", new { userName = "bob", password = Password }, Ct);
            await AssertErrorAsync(locked, HttpStatusCode.Unauthorized, "locked");
        }

        var resp = await admin.PostAsJsonAsync($"/api/admin/users/{bob.Id}/reset-password", new { newPassword = OtherPassword }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal("change-password", await LoginNextAsync(f, "bob", OtherPassword));

        await AssertErrorAsync(await admin.PostAsJsonAsync($"/api/admin/users/{bob.Id}/reset-password", new { newPassword = "short" }, Ct),
            HttpStatusCode.BadRequest, "password_too_short");
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        Assert.Contains(await db.AuditLogs.ToListAsync(Ct), a => a.Action == "user.reset_password" && a.Target == "bob");
    }

    [Fact]
    public async Task Clear_totp_disables_two_factor()
    {
        var (f, _, admin) = await AdminAsync();
        var bob = await LocalAuth.SeedUserAsync(f, "bob", Password, UserRole.Analyst);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByIdAsync(bob.Id.ToString()))!;
            await users.ResetAuthenticatorKeyAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
            await users.SetAuthenticationTokenAsync(user, "[AspNetUserStore]", "RecoveryCodes", "AAAA;BBBB");
        }
        Assert.Equal("totp", await LoginNextAsync(f, "bob", Password));

        var resp = await admin.PostAsync($"/api/admin/users/{bob.Id}/clear-totp", null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal("done", await LoginNextAsync(f, "bob", Password));

        await using var check = f.Services.CreateAsyncScope();
        var um = check.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var reloaded = (await um.FindByIdAsync(bob.Id.ToString()))!;
        Assert.False(await um.GetTwoFactorEnabledAsync(reloaded));
        Assert.Null(await um.GetAuthenticationTokenAsync(reloaded, "[AspNetUserStore]", "RecoveryCodes"));
        var db = check.ServiceProvider.GetRequiredService<MaldexDbContext>();
        Assert.Contains(await db.AuditLogs.ToListAsync(Ct), a => a.Action == "user.clear_totp" && a.Target == "bob");
    }

    [Fact]
    public async Task Role_change_applies_at_once()
    {
        // Default stamp interval (5 minutes): the role is read from the row on every request, so a change applies immediately.
        var (f, _, admin) = await AdminAsync();
        var bob = await LocalAuth.SeedUserAsync(f, "bob", Password, UserRole.Analyst);
        using var target = await LocalAuth.SignInAsync(f, "bob", Password);
        Assert.Equal("Analyst", (await target.GetFromJsonAsync<JsonElement>("/api/me", Ct)).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(admin, bob.Id, new { role = "Admin" })).StatusCode);
        Assert.Equal("Admin", (await target.GetFromJsonAsync<JsonElement>("/api/me", Ct)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Changes_end_target_sessions_at_the_next_stamp_check()
    {
        var (f, _, admin) = await AdminAsync(new() { ["Maldex:Auth:StampValidationInterval"] = "00:00:00" });
        var bob = await LocalAuth.SeedUserAsync(f, "bob", Password, UserRole.Analyst);
        using var target = await LocalAuth.SignInAsync(f, "bob", Password);
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/me", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsJsonAsync($"/api/admin/users/{bob.Id}/reset-password", new { newPassword = OtherPassword }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/me", Ct)).StatusCode);

        // The reset forces a password change, so finish that sign-in first; the role change then ends the new session too.
        using var again = f.CreateClient();
        await again.PostAsJsonAsync("/api/auth/login", new { userName = "bob", password = OtherPassword }, Ct);
        var changed = await again.PostAsJsonAsync("/api/auth/password", new { currentPassword = OtherPassword, newPassword = Password }, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(admin, bob.Id, new { role = "Admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Settings_round_trip_and_audit()
    {
        var (f, _, admin) = await AdminAsync();
        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/admin/settings", Ct)).GetProperty("requireTotpForLocal").GetBoolean());
        var put = await admin.PutAsJsonAsync("/api/admin/settings", new { requireTotpForLocal = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await JsonAsync(put)).GetProperty("requireTotpForLocal").GetBoolean());
        Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/admin/settings", Ct)).GetProperty("requireTotpForLocal").GetBoolean());

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        Assert.Contains(await db.AuditLogs.ToListAsync(Ct), a => a.Action == "settings.update");
    }

    [Fact]
    public async Task Concurrent_demotions_of_the_only_two_admins_never_both_succeed()
    {
        for (var round = 0; round < 3; round++)
        {
            var f = Factory();
            var a = await LocalAuth.SeedUserAsync(f, "admin-a", Password, UserRole.Admin);
            var b = await LocalAuth.SeedUserAsync(f, "admin-b", Password, UserRole.Admin);
            using var ca = await LocalAuth.SignInAsync(f, "admin-a", Password);
            using var cb = await LocalAuth.SignInAsync(f, "admin-b", Password);

            var results = await Task.WhenAll(PatchAsync(ca, b.Id, new { role = "Analyst" }), PatchAsync(cb, a.Id, new { role = "Analyst" }));
            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));

            await using var scope = f.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
            Assert.Equal(1, await db.Users.CountAsync(u => u.Role == UserRole.Admin, Ct));
        }
    }

    [Fact]
    public async Task Role_and_disable_in_one_patch_of_the_last_admin_is_refused()
    {
        var (_, root, admin) = await AdminAsync();
        await AssertErrorAsync(await PatchAsync(admin, root.Id, new { role = "Analyst", isDisabled = true }), HttpStatusCode.Conflict, "last_admin");
    }

    [Fact]
    public async Task Update_that_changes_nothing_keeps_the_stamp_and_writes_no_audit_row()
    {
        var (f, _, admin) = await AdminAsync();
        var bob = await LocalAuth.SeedUserAsync(f, "bob", Password, UserRole.Analyst);
        string StampOf() { using var s = f.Services.CreateScope(); return s.ServiceProvider.GetRequiredService<MaldexDbContext>().Users.AsNoTracking().Single(u => u.Id == bob.Id).SecurityStamp!; }
        var before = StampOf();

        var resp = await PatchAsync(admin, bob.Id, new { displayName = "bob", role = "Analyst", isDisabled = false });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("bob", (await JsonAsync(resp)).GetProperty("userName").GetString());
        Assert.Equal(before, StampOf());
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        Assert.DoesNotContain(await db.AuditLogs.ToListAsync(Ct), a => a.Action == "user.update");
    }

    [Fact]
    public async Task Audit_rows_carry_the_actor_and_note_an_auto_cleared_break_glass()
    {
        var (f, root, admin) = await AdminAsync();
        var glass = await LocalAuth.SeedUserAsync(f, "glass", Password, UserRole.Admin, u => u.IsBreakGlass = true);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(admin, glass.Id, new { role = "Analyst" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(admin, "carol")).StatusCode);

        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        var rows = await db.AuditLogs.Where(a => a.Action.StartsWith("user.")).ToListAsync(Ct);
        Assert.NotEmpty(rows);
        Assert.All(rows, a => Assert.Equal(root.Id, a.UserId));
        Assert.Contains("isBreakGlass -> False (cleared: role no longer Admin)", rows.Single(a => a.Action == "user.update").Details);
    }
}

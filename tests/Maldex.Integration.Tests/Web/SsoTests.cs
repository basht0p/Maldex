using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Web;

/// <summary>Generic OIDC single sign-on (<c>Maldex:Auth:Oidc</c>) against the in-process <see cref="FakeOidcProvider"/>.</summary>
[Collection("sql")]
public sealed class SsoTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const string ClientId = "maldex-test";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IDisposable> _disposables = [];
    private readonly List<FakeOidcProvider> _fakes = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _disposables) d.Dispose();
        foreach (var f in _fakes) await f.DisposeAsync();
    }

    private async Task<FakeOidcProvider> FakeAsync()
    {
        var fake = await FakeOidcProvider.StartAsync();
        _fakes.Add(fake);
        return fake;
    }

    private MaldexAppFactory Factory(FakeOidcProvider? fake, Dictionary<string, string?>? extra = null, string? connectionString = null)
    {
        var settings = new Dictionary<string, string?>();
        if (fake is not null)
        {
            settings["Maldex:Auth:Oidc:Enabled"] = "true";
            settings["Maldex:Auth:Oidc:DisplayName"] = "Test IdP";
            settings["Maldex:Auth:Oidc:Authority"] = fake.Authority;
            settings["Maldex:Auth:Oidc:ClientId"] = ClientId;
        }
        if (extra is not null)
            foreach (var (k, v) in extra) settings[k] = v;
        var f = new MaldexAppFactory(sql, authMode: "Local", settings: settings, connectionString: connectionString);
        _disposables.Add(f);
        return f;
    }

    private static Dictionary<string, string?> GroupMapping() => new()
    {
        ["Maldex:Auth:Oidc:RoleClaim"] = "groups",
        ["Maldex:Auth:Oidc:AdminValues:0"] = "g-admin",
        ["Maldex:Auth:Oidc:AnalystValues:0"] = "g-ana",
    };

    private static Dictionary<string, string[]> Groups(params string[] groups) => new() { ["groups"] = groups };

    private static HttpClient AppClient(MaldexAppFactory f) => f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Start → the fake's authorize → the callback; returns the callback's redirect and the app client (with its cookies).</summary>
    private static Task<(string location, HttpClient client)> SignInViaSsoAsync(MaldexAppFactory f, FakeOidcProvider fake, string returnUrl = "/") =>
        SignInViaSsoAsync(AppClient(f), returnUrl);

    /// <summary>As above, on an existing app client (keeping any session it already has).</summary>
    private static async Task<(string location, HttpClient client)> SignInViaSsoAsync(HttpClient client, string returnUrl = "/")
    {
        var callback = await AuthorizeAsync(client, returnUrl);
        return (await CallbackAsync(client, callback), client);
    }

    /// <summary>Steps 1–2: the start endpoint, then the fake's authorize; returns the callback URL the IdP sent the browser to.</summary>
    private static async Task<Uri> AuthorizeAsync(HttpClient client, string returnUrl)
    {
        var start = await client.GetAsync("/api/auth/sso/start?returnUrl=" + Uri.EscapeDataString(returnUrl), Ct);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        AssertNoExternalCookie(start);
        using var plain = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var authorize = await plain.GetAsync(start.Headers.Location!, Ct);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        return authorize.Headers.Location!;
    }

    /// <summary>Step 3: the callback on the app; returns its redirect.</summary>
    private static async Task<string> CallbackAsync(HttpClient client, Uri callback)
    {
        var resp = await client.GetAsync(callback, Ct);
        Assert.True(resp.StatusCode == HttpStatusCode.Redirect, $"callback answered {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(Ct)}");
        AssertNoExternalCookie(resp);
        return resp.Headers.Location!.OriginalString;
    }

    /// <summary>The handler's sign-in cookie (Maldex.External) is never written: the callback issues the Maldex session itself.</summary>
    private static void AssertNoExternalCookie(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("Set-Cookie", out var cookies))
            Assert.DoesNotContain(cookies, c => c.Contains("maldex-external", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<JsonElement> MeAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/me", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<T> DbAsync<T>(MaldexAppFactory f, Func<MaldexDbContext, Task<T>> query)
    {
        await using var scope = f.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<MaldexDbContext>());
    }

    private static Task UpdateUserAsync(MaldexAppFactory f, Guid id, Action<AppUser> change) => DbAsync(f, async db =>
    {
        change(await db.Users.SingleAsync(u => u.Id == id, Ct));
        return await db.SaveChangesAsync(Ct);
    });

    private static Task<List<AppUser>> SsoUsersAsync(MaldexAppFactory f) =>
        DbAsync(f, db => db.Users.AsNoTracking().Where(u => u.Kind == UserKind.Sso).ToListAsync(Ct));

    [Fact]
    public async Task First_sign_in_creates_a_pending_sso_user()
    {
        var fake = await FakeAsync();
        var f = Factory(fake);
        fake.NextUser("sub-1", "Sam Sso", "sam@example.test");

        var (location, client) = await SignInViaSsoAsync(f, fake);

        Assert.Equal("/", location);
        var me = await MeAsync(client);
        Assert.Equal(JsonValueKind.Null, me.GetProperty("role").ValueKind);
        Assert.Equal("Sso", me.GetProperty("kind").GetString());
        Assert.Equal("Sam Sso", me.GetProperty("displayName").GetString());
        var tasks = await client.GetAsync("/api/tasks", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, tasks.StatusCode);
        Assert.Equal("no_role", (await tasks.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());

        var user = Assert.Single(await SsoUsersAsync(f));
        Assert.Matches("^sso-[0-9a-f]{8}$", user.UserName);
        Assert.Null(user.PasswordHash);
        Assert.NotNull(user.LastSignInAt);
        var login = await DbAsync(f, db => db.UserLogins.AsNoTracking().SingleAsync(Ct));
        Assert.Equal("oidc:" + fake.Authority, login.LoginProvider);
        Assert.Equal("sub-1", login.ProviderKey);
        Assert.Equal(user.Id, login.UserId);
        Assert.True(await DbAsync(f, db => db.AuditLogs.AnyAsync(a => a.Action == "auth.sso_login" && a.UserId == user.Id, Ct)));
    }

    [Fact]
    public async Task Admin_assigned_role_is_kept_without_mapping()
    {
        var fake = await FakeAsync();
        var f = Factory(fake);
        fake.NextUser("sub-keep", "Keeper");
        await SignInViaSsoAsync(f, fake);
        var user = Assert.Single(await SsoUsersAsync(f));
        await UpdateUserAsync(f, user.Id, u => u.Role = UserRole.Analyst);

        var (location, client) = await SignInViaSsoAsync(f, fake, "/tasks");

        Assert.Equal("/tasks", location);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tasks", Ct)).StatusCode);
        Assert.Equal("Analyst", (await MeAsync(client)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Mapped_admin_and_analyst()
    {
        var fake = await FakeAsync();
        var f = Factory(fake, GroupMapping());

        fake.NextUser("sub-map", "Mapped", extraClaims: Groups("g-ana"));
        var (_, analyst) = await SignInViaSsoAsync(f, fake);
        var me = await MeAsync(analyst);
        Assert.Equal("Analyst", me.GetProperty("role").GetString());
        Assert.True(me.GetProperty("roleManagedByIdp").GetBoolean());

        fake.NextUser("sub-map", "Mapped", extraClaims: Groups("g-admin", "g-ana"));
        var (_, admin) = await SignInViaSsoAsync(f, fake);
        Assert.Equal("Admin", (await MeAsync(admin)).GetProperty("role").GetString());
        Assert.Single(await SsoUsersAsync(f));
    }

    [Fact]
    public async Task Unmapped_user_is_refused_but_listed()
    {
        var fake = await FakeAsync();
        var f = Factory(fake, GroupMapping());
        fake.NextUser("sub-other", "Other", extraClaims: Groups("other"));

        var (location, client) = await SignInViaSsoAsync(f, fake);

        Assert.Equal("/login?error=no_role", location);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
        var user = Assert.Single(await SsoUsersAsync(f));
        Assert.Null(user.Role);
        Assert.Equal("Other", user.DisplayName);
        Assert.True(await DbAsync(f, db => db.AuditLogs.AnyAsync(a => a.Action == "auth.sso_refused" && a.Details == "no_role" && a.UserId == user.Id, Ct)));

        // A mapped user who loses the group loses the role, and is refused.
        fake.NextUser("sub-lost", "Lost", extraClaims: Groups("g-admin"));
        await SignInViaSsoAsync(f, fake);
        fake.NextUser("sub-lost", "Lost", extraClaims: Groups("other"));
        Assert.Equal("/login?error=no_role", (await SignInViaSsoAsync(f, fake)).location);
        Assert.Null((await SsoUsersAsync(f)).Single(u => u.DisplayName == "Lost").Role);
    }

    [Fact]
    public async Task Role_claim_as_a_single_string_is_mapped()
    {
        var fake = await FakeAsync();
        var f = Factory(fake, GroupMapping());
        fake.NextUser("sub-single", "Single", extraClaims: Groups("g-admin"), singleValuesAsStrings: true);

        var (location, client) = await SignInViaSsoAsync(f, fake);

        Assert.Equal("/", location);
        Assert.Equal("Admin", (await MeAsync(client)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Configured_role_values_are_trimmed_and_matched_ordinally()
    {
        var fake = await FakeAsync();
        var f = Factory(fake, new()
        {
            ["Maldex:Auth:Oidc:RoleClaim"] = "groups",
            ["Maldex:Auth:Oidc:AdminValues:0"] = "  g-admin  ",
            ["Maldex:Auth:Oidc:AdminValues:1"] = " ",
            ["Maldex:Auth:Oidc:AnalystValues:0"] = "",
            ["Maldex:Auth:Oidc:AnalystValues:1"] = "g-ana",
        });

        fake.NextUser("sub-trim", "Trim", extraClaims: Groups("g-admin"));
        Assert.Equal("Admin", (await MeAsync((await SignInViaSsoAsync(f, fake)).client)).GetProperty("role").GetString());

        // Case-sensitive: G-ANA is not g-ana, and a blank configured value never matches a blank claim value.
        fake.NextUser("sub-case", "Case", extraClaims: Groups("G-ANA", "", " "));
        Assert.Equal("/login?error=no_role", (await SignInViaSsoAsync(f, fake)).location);
    }

    [Fact]
    public async Task Mapped_role_change_rotates_the_security_stamp()
    {
        var fake = await FakeAsync();
        var f = Factory(fake, GroupMapping());
        fake.NextUser("sub-stamp", "Stamp", extraClaims: Groups("g-ana"));
        await SignInViaSsoAsync(f, fake);
        var first = Assert.Single(await SsoUsersAsync(f)).SecurityStamp;

        await SignInViaSsoAsync(f, fake);
        Assert.Equal(first, Assert.Single(await SsoUsersAsync(f)).SecurityStamp);

        fake.NextUser("sub-stamp", "Stamp", extraClaims: Groups("g-admin"));
        await SignInViaSsoAsync(f, fake);
        var user = Assert.Single(await SsoUsersAsync(f));
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.NotEqual(first, user.SecurityStamp);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    public async Task Blank_sub_is_refused_as_sso_failed(string sub)
    {
        var fake = await FakeAsync();
        var f = Factory(fake);
        fake.NextUser(sub, "No Sub");

        var (location, client) = await SignInViaSsoAsync(f, fake);

        Assert.Equal("/login?error=sso_failed", location);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Empty(await SsoUsersAsync(f));
        Assert.Equal(0, await DbAsync(f, db => db.UserLogins.CountAsync(Ct)));
    }

    [Fact]
    public async Task Missing_or_blank_sub_is_refused_and_audited_as_no_sub()
    {
        // The OIDC handler already rejects such tokens (above); CompleteAsync must refuse them too, never throw.
        var fake = await FakeAsync();
        var f = Factory(fake);
        var options = new Maldex.Web.Auth.OidcOptions { Enabled = true, Authority = fake.Authority, ClientId = ClientId };
        foreach (var claims in new[] { new[] { new Claim("iss", fake.Authority) }, [new Claim("iss", fake.Authority), new Claim("sub", "  ")] })
        {
            await using var scope = f.Services.CreateAsyncScope();
            var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            var target = await Maldex.Web.Auth.SsoSignIn.CompleteAsync(ctx, new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc")), options, "/tasks");
            Assert.Equal("/login?error=sso_failed", target);
        }
        Assert.Empty(await SsoUsersAsync(f));
        Assert.Equal(2, await DbAsync(f, db => db.AuditLogs.CountAsync(a => a.Action == "auth.sso_refused" && a.Details == "no_sub" && a.UserId == null, Ct)));
    }

    [Fact]
    public async Task Refusal_ends_an_existing_session()
    {
        var fake = await FakeAsync();
        var f = Factory(fake, GroupMapping());
        fake.NextUser("sub-first", "First", extraClaims: Groups("g-ana"));
        var (_, client) = await SignInViaSsoAsync(f, fake);
        Assert.Equal("Analyst", (await MeAsync(client)).GetProperty("role").GetString());

        fake.NextUser("sub-second", "Second", extraClaims: Groups("other"));
        var (location, _) = await SignInViaSsoAsync(client);

        Assert.Equal("/login?error=no_role", location);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Disabled_sso_user_is_refused()
    {
        var fake = await FakeAsync();
        var f = Factory(fake);
        fake.NextUser("sub-off", "Off");
        await SignInViaSsoAsync(f, fake);
        var user = Assert.Single(await SsoUsersAsync(f));
        await UpdateUserAsync(f, user.Id, u => { u.IsDisabled = true; u.Role = UserRole.Analyst; });

        var (location, client) = await SignInViaSsoAsync(f, fake);

        Assert.Equal("/login?error=disabled", location);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.True(await DbAsync(f, db => db.AuditLogs.AnyAsync(a => a.Action == "auth.sso_refused" && a.Details == "disabled" && a.UserId == user.Id, Ct)));
    }

    [Fact]
    public async Task No_linking_to_local_user_with_same_email()
    {
        var fake = await FakeAsync();
        var f = Factory(fake);
        var alice = await LocalAuth.SeedUserAsync(f, "alice", "correct horse battery", UserRole.Admin, u => u.Email = "alice@x");
        fake.NextUser("alice", "alice", "alice@x", new Dictionary<string, string[]> { ["preferred_username"] = ["alice"] });

        var (location, client) = await SignInViaSsoAsync(f, fake);

        Assert.Equal("/", location);
        var me = await MeAsync(client);
        Assert.NotEqual(alice.Id, me.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("role").ValueKind);
        var sso = Assert.Single(await SsoUsersAsync(f));
        Assert.StartsWith("sso-", sso.UserName);
        Assert.Equal(2, await DbAsync(f, db => db.Users.CountAsync(Ct)));
    }

    [Fact]
    public async Task Authority_change_creates_a_new_user()
    {
        var fake1 = await FakeAsync();
        var fake2 = await FakeAsync();
        var f1 = Factory(fake1);
        var f2 = Factory(fake2, connectionString: f1.ConnectionString);
        fake1.NextUser("same-sub", "Same");
        fake2.NextUser("same-sub", "Same");

        var (_, c1) = await SignInViaSsoAsync(f1, fake1);
        var (_, c2) = await SignInViaSsoAsync(f2, fake2);

        Assert.NotEqual((await MeAsync(c1)).GetProperty("id").GetGuid(), (await MeAsync(c2)).GetProperty("id").GetGuid());
        Assert.Equal(2, (await SsoUsersAsync(f1)).Count);
        var providers = await DbAsync(f1, db => db.UserLogins.AsNoTracking().Select(l => l.LoginProvider).OrderBy(p => p).ToListAsync(Ct));
        Assert.Equal(new[] { "oidc:" + fake1.Authority, "oidc:" + fake2.Authority }.Order(), providers);
    }

    [Fact]
    public async Task Public_client_without_secret_uses_pkce()
    {
        var fake = await FakeAsync();
        fake.RequireClientSecret(null);
        var f = Factory(fake, new() { ["Maldex:Auth:Oidc:ClientSecret"] = "" });
        fake.NextUser("sub-public", "Public");

        var (location, client) = await SignInViaSsoAsync(f, fake);

        Assert.Equal("/", location);
        Assert.Equal("Sso", (await MeAsync(client)).GetProperty("kind").GetString());
        var token = fake.LastTokenRequest!;
        Assert.False(string.IsNullOrEmpty(token["code_verifier"]));
        Assert.True(!token.TryGetValue("client_secret", out var secret) || string.IsNullOrEmpty(secret));
    }

    [Fact]
    public async Task Confidential_client_sends_secret()
    {
        var fake = await FakeAsync();
        fake.RequireClientSecret("s3cret");
        var good = Factory(fake, new() { ["Maldex:Auth:Oidc:ClientSecret"] = "s3cret" });
        fake.NextUser("sub-conf", "Confidential");

        var (location, client) = await SignInViaSsoAsync(good, fake);
        Assert.Equal("/", location);
        Assert.Equal("Sso", (await MeAsync(client)).GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(fake.LastTokenRequest!["code_verifier"]));

        var bad = Factory(fake, new() { ["Maldex:Auth:Oidc:ClientSecret"] = "wrong" });
        var (badLocation, badClient) = await SignInViaSsoAsync(bad, fake);
        Assert.Equal("/login?error=sso_failed", badLocation);
        Assert.Equal(HttpStatusCode.Unauthorized, (await badClient.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("evil.example")]
    [InlineData("/\t/evil.example")]
    public async Task Sso_start_rejects_non_local_return_urls(string returnUrl)
    {
        var fake = await FakeAsync();
        var f = Factory(fake);
        fake.NextUser("sub-redirect", "Redirect");

        var (location, _) = await SignInViaSsoAsync(f, fake, returnUrl);

        Assert.Equal("/", location);
    }

    [Fact]
    public async Task Sso_start_is_404_when_disabled()
    {
        var f = Factory(null);
        using var client = AppClient(f);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/auth/sso/start?returnUrl=%2F", Ct)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_first_sign_in_creates_one_user()
    {
        var fake = await FakeAsync();
        var f = Factory(fake);
        fake.NextUser("sub-race", "Racer");
        var a = AppClient(f);
        var b = AppClient(f);
        var callbackA = await AuthorizeAsync(a, "/");
        var callbackB = await AuthorizeAsync(b, "/");

        var locations = await Task.WhenAll(CallbackAsync(a, callbackA), CallbackAsync(b, callbackB));

        Assert.All(locations, l => Assert.Equal("/", l));
        Assert.Equal(1, await DbAsync(f, db => db.UserLogins.CountAsync(Ct)));
        var user = Assert.Single(await SsoUsersAsync(f));
        Assert.Equal(user.Id, (await MeAsync(a)).GetProperty("id").GetGuid());
        Assert.Equal(user.Id, (await MeAsync(b)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Long_or_missing_names_fall_back_and_truncate()
    {
        var fake = await FakeAsync();
        var f = Factory(fake);

        fake.NextUser("sub-long", new string('n', 300));
        await SignInViaSsoAsync(f, fake);
        Assert.Equal(new string('n', 200), (await SsoUsersAsync(f)).Single().DisplayName);

        // A surrogate pair straddling the 200-character cut is dropped whole, never split.
        fake.NextUser("sub-emoji", new string('e', 199) + "\U0001F600" + "tail");
        var (_, emoji) = await SignInViaSsoAsync(f, fake);
        Assert.Equal(new string('e', 199), (await MeAsync(emoji)).GetProperty("displayName").GetString());

        fake.NextUser("sub-bare");
        var (_, client) = await SignInViaSsoAsync(f, fake);
        Assert.Equal("sub-bare", (await MeAsync(client)).GetProperty("displayName").GetString());

        fake.NextUser("sub-pref", extraClaims: new Dictionary<string, string[]> { ["preferred_username"] = ["pref"] });
        var (_, pref) = await SignInViaSsoAsync(f, fake);
        Assert.Equal("pref", (await MeAsync(pref)).GetProperty("displayName").GetString());

        fake.NextUser("sub-mail", email: "mail@example.test");
        var (_, mail) = await SignInViaSsoAsync(f, fake);
        Assert.Equal("mail@example.test", (await MeAsync(mail)).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Configured_name_claim_is_preferred()
    {
        var fake = await FakeAsync();
        var f = Factory(fake, new() { ["Maldex:Auth:Oidc:NameClaim"] = "nickname" });
        fake.NextUser("sub-nick", "Full Name", extraClaims: new Dictionary<string, string[]> { ["nickname"] = ["Nick"] });

        var (_, client) = await SignInViaSsoAsync(f, fake);

        Assert.Equal("Nick", (await MeAsync(client)).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Config_reports_sso_display_name()
    {
        var fake = await FakeAsync();
        using var client = Factory(fake).CreateClient();
        var config = await client.GetFromJsonAsync<JsonElement>("/api/auth/config", Ct);
        Assert.Equal("Test IdP", config.GetProperty("sso").GetProperty("displayName").GetString());

        using var plain = Factory(null).CreateClient();
        var none = await plain.GetFromJsonAsync<JsonElement>("/api/auth/config", Ct);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("sso").ValueKind);
    }
}

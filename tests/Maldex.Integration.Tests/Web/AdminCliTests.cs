using System.Net.Http.Json;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Integration.Tests.Fixtures;
using Maldex.Web.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Maldex.Integration.Tests.Web;

/// <summary>The <c>Maldex.Web admin</c> CLI, run in process against a fixture database.</summary>
[Collection("sql")]
public sealed class AdminCliTests(SqlServerFixture sql) : IDisposable
{
    private const string Password = "correct horse battery";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private sealed class FakePrompt(params string[] answers) : IPasswordPrompt
    {
        private readonly Queue<string> _answers = new(answers);
        public List<string> Asked { get; } = [];

        public string ReadPassword(string prompt)
        {
            Asked.Add(prompt);
            return _answers.Dequeue();
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Maldex.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed record Result(int Exit, string Out, string Err);

    private static IConfiguration Config(string cs, bool passwordLogin = true, bool oidc = false) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Maldex"] = cs,
            ["Maldex:Auth:PasswordLogin"] = passwordLogin ? "true" : "false",
            ["Maldex:Auth:Oidc:Enabled"] = oidc ? "true" : "false",
        }).Build();

    private static async Task<Result> RunAsync(string cs, string[] args, string stdin = "", IPasswordPrompt? prompt = null, IConfiguration? config = null)
    {
        var @out = new StringWriter();
        var err = new StringWriter();
        var exit = await AdminCli.RunAsync(args, config ?? Config(cs), new TestEnvironment(), new StringReader(stdin), @out, err, prompt ?? new FakePrompt(), Ct);
        return new Result(exit, @out.ToString(), err.ToString());
    }

    private async Task<string> MigratedDbAsync()
    {
        var cs = sql.CreateDatabaseConnectionString();
        await using var db = sql.CreateContext(cs);
        await db.Database.MigrateAsync(Ct);
        return cs;
    }

    private async Task<HttpResponseMessage> LoginAsync(string cs, string userName, string password)
    {
        var f = new MaldexAppFactory(sql, authMode: "Local", connectionString: cs);
        _disposables.Add(f);
        return await f.CreateClient().PostAsJsonAsync("/api/auth/login", new { userName, password }, Ct);
    }

    /// <summary>Signs in and returns the answer's <c>next</c> step; the sign-in must succeed.</summary>
    private async Task<string> NextAfterLoginAsync(string cs, string userName, string password)
    {
        var resp = await LoginAsync(cs, userName, password);
        Assert.True(resp.IsSuccessStatusCode, $"login as '{userName}' failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(Ct)}");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("next").GetString()!;
    }

    private async Task<AppUser> UserAsync(string cs, string name)
    {
        await using var db = sql.CreateContext(cs);
        return await db.Users.AsNoTracking().SingleAsync(u => u.NormalizedUserName == name.ToUpperInvariant(), Ct);
    }

    [Fact]
    public async Task Create_admin_then_login_does_not_require_password_change()
    {
        var cs = await MigratedDbAsync();
        var r = await RunAsync(cs, ["create-user", "alice", "--admin", "--display-name", "Alice A", "--password-stdin"], Password + "\n");
        Assert.True(r.Exit == 0, r.Out + r.Err);

        var alice = await UserAsync(cs, "alice");
        Assert.Equal(UserRole.Admin, alice.Role);
        Assert.Equal("Alice A", alice.DisplayName);
        Assert.False(alice.MustChangePassword);
        Assert.NotEqual(LoginSteps.ChangePassword, await NextAfterLoginAsync(cs, "alice", Password));
    }

    [Fact]
    public async Task Create_reads_password_from_prompt_twice_and_refuses_mismatch()
    {
        var cs = await MigratedDbAsync();
        var ok = new FakePrompt(Password, Password);
        var r = await RunAsync(cs, ["create-user", "bob"], prompt: ok);
        Assert.True(r.Exit == 0, r.Out + r.Err);
        Assert.Equal(["New password: ", "Repeat password: "], ok.Asked);
        Assert.Equal(UserRole.Analyst, (await UserAsync(cs, "bob")).Role);
        Assert.NotEqual(LoginSteps.ChangePassword, await NextAfterLoginAsync(cs, "bob", Password));

        var mismatch = await RunAsync(cs, ["create-user", "carol"], prompt: new FakePrompt(Password, Password + "x"));
        Assert.Equal(1, mismatch.Exit);
        Assert.Contains("Passwords do not match", mismatch.Err);
        await using var db = sql.CreateContext(cs);
        Assert.False(await db.Users.AnyAsync(u => u.NormalizedUserName == "CAROL", Ct));
    }

    [Fact]
    public async Task Password_stdin_strips_only_one_trailing_newline()
    {
        var cs = await MigratedDbAsync();
        Assert.Equal(0, (await RunAsync(cs, ["create-user", "unix", "--password-stdin"], "abc def ghi jkl\n")).Exit);
        Assert.Equal(0, (await RunAsync(cs, ["create-user", "windows", "--password-stdin"], "abc def ghi jkl \r\n")).Exit);
        Assert.Equal(0, (await RunAsync(cs, ["create-user", "bare", "--password-stdin"], "abc def ghi jkl")).Exit);
        Assert.NotEqual(LoginSteps.ChangePassword, await NextAfterLoginAsync(cs, "unix", "abc def ghi jkl"));
        // Exactly one terminator was stripped: the trailing space of the second password is kept.
        Assert.NotEqual(LoginSteps.ChangePassword, await NextAfterLoginAsync(cs, "windows", "abc def ghi jkl "));
        Assert.NotEqual(LoginSteps.ChangePassword, await NextAfterLoginAsync(cs, "bare", "abc def ghi jkl"));
        Assert.False((await LoginAsync(cs, "windows", "abc def ghi jkl")).IsSuccessStatusCode);
    }

    [Theory]
    [InlineData("--password", "foo")]
    [InlineData("--password=foo", "")]
    public async Task Password_argument_is_refused(string first, string second)
    {
        var cs = await MigratedDbAsync();
        var r = await RunAsync(cs, second == "" ? ["create-user", "x", first] : ["create-user", "x", first, second]);
        Assert.Equal(64, r.Exit);
        Assert.Contains("--password-stdin", r.Err);
        await using var db = sql.CreateContext(cs);
        Assert.False(await db.Users.AnyAsync(u => u.NormalizedUserName == "X", Ct));
    }

    [Fact]
    public async Task Reset_password_clears_lockout_and_optionally_totp()
    {
        var cs = await MigratedDbAsync();
        Assert.Equal(0, (await RunAsync(cs, ["create-user", "dave", "--password-stdin"], Password)).Exit);
        await using (var db = sql.CreateContext(cs))
        {
            var u = await db.Users.SingleAsync(x => x.NormalizedUserName == "DAVE", Ct);
            u.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(10);
            u.AccessFailedCount = 5;
            u.TwoFactorEnabled = true;
            await db.SaveChangesAsync(Ct);
        }
        var stampBefore = (await UserAsync(cs, "dave")).SecurityStamp;

        var plain = await RunAsync(cs, ["reset-password", "dave", "--password-stdin"], "a brand new password\n");
        Assert.True(plain.Exit == 0, plain.Out + plain.Err);
        var after = await UserAsync(cs, "dave");
        Assert.True(after.LockoutEnd is null || after.LockoutEnd <= DateTimeOffset.UtcNow);
        Assert.Equal(0, after.AccessFailedCount);
        Assert.True(after.TwoFactorEnabled);
        Assert.NotEqual(stampBefore, after.SecurityStamp);
        Assert.False(after.MustChangePassword);

        var withTotp = await RunAsync(cs, ["reset-password", "dave", "--reset-totp", "--password-stdin"], "yet another password\n");
        Assert.True(withTotp.Exit == 0, withTotp.Out + withTotp.Err);
        Assert.False((await UserAsync(cs, "dave")).TwoFactorEnabled);
        Assert.NotEqual(LoginSteps.ChangePassword, await NextAfterLoginAsync(cs, "dave", "yet another password"));

        var missing = await RunAsync(cs, ["reset-password", "nobody", "--password-stdin"], Password);
        Assert.Equal(1, missing.Exit);
        Assert.Contains("error: not_found", missing.Err);
    }

    [Fact]
    public async Task Set_break_glass_requires_local_admin()
    {
        var cs = await MigratedDbAsync();
        Assert.Equal(0, (await RunAsync(cs, ["create-user", "ann", "--analyst", "--password-stdin"], Password)).Exit);
        Assert.Equal(0, (await RunAsync(cs, ["create-user", "root1", "--admin", "--password-stdin"], Password)).Exit);

        var refused = await RunAsync(cs, ["set-break-glass", "ann", "on"]);
        Assert.Equal(1, refused.Exit);
        Assert.StartsWith("error: break_glass_requires_local_admin: ", refused.Err);

        Assert.Equal(0, (await RunAsync(cs, ["set-break-glass", "root1", "on"])).Exit);
        Assert.True((await UserAsync(cs, "root1")).IsBreakGlass);
        Assert.Equal(0, (await RunAsync(cs, ["set-break-glass", "root1", "off"])).Exit);
        Assert.False((await UserAsync(cs, "root1")).IsBreakGlass);
        Assert.Equal(64, (await RunAsync(cs, ["set-break-glass", "root1", "maybe"])).Exit);
    }

    [Fact]
    public async Task Disable_last_admin_is_refused()
    {
        var cs = await MigratedDbAsync();
        Assert.Equal(0, (await RunAsync(cs, ["create-user", "boss", "--admin", "--password-stdin"], Password)).Exit);
        var refused = await RunAsync(cs, ["disable", "boss"]);
        Assert.Equal(1, refused.Exit);
        Assert.StartsWith("error: last_admin: ", refused.Err);
        Assert.False((await UserAsync(cs, "boss")).IsDisabled);

        Assert.Equal(0, (await RunAsync(cs, ["create-user", "boss2", "--admin", "--password-stdin"], Password)).Exit);
        Assert.Equal(0, (await RunAsync(cs, ["disable", "boss"])).Exit);
        Assert.True((await UserAsync(cs, "boss")).IsDisabled);
        Assert.Equal(0, (await RunAsync(cs, ["enable", "boss"])).Exit);
        Assert.False((await UserAsync(cs, "boss")).IsDisabled);
    }

    [Fact]
    public async Task Set_role_none_and_back()
    {
        var cs = await MigratedDbAsync();
        Assert.Equal(0, (await RunAsync(cs, ["create-user", "eve", "--password-stdin"], Password)).Exit);
        Assert.Equal(0, (await RunAsync(cs, ["set-role", "eve", "none"])).Exit);
        Assert.Null((await UserAsync(cs, "eve")).Role);
        Assert.Equal(0, (await RunAsync(cs, ["set-role", "eve", "admin"])).Exit);
        Assert.Equal(UserRole.Admin, (await UserAsync(cs, "eve")).Role);
        Assert.Equal(0, (await RunAsync(cs, ["create-user", "eve2", "--admin", "--password-stdin"], Password)).Exit);
        Assert.Equal(0, (await RunAsync(cs, ["set-role", "eve", "analyst"])).Exit);
        Assert.Equal(UserRole.Analyst, (await UserAsync(cs, "eve")).Role);
        Assert.Equal(64, (await RunAsync(cs, ["set-role", "eve", "wizard"])).Exit);
    }

    [Fact]
    public async Task List_json_reports_users_and_config()
    {
        var cs = await MigratedDbAsync();
        Assert.Equal(0, (await RunAsync(cs, ["create-user", "zed", "--admin", "--break-glass", "--display-name", "Zed Z", "--password-stdin"], Password)).Exit);
        Assert.Equal(0, (await RunAsync(cs, ["create-user", "amy", "--password-stdin"], Password)).Exit);

        var r = await RunAsync(cs, ["list", "--json"], config: Config(cs, passwordLogin: false, oidc: true));
        Assert.Equal(0, r.Exit);
        var lines = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("#config ", lines[^1]);
        var config = JsonDocument.Parse(lines[^1]["#config ".Length..]).RootElement;
        Assert.False(config.GetProperty("passwordLogin").GetBoolean());
        Assert.True(config.GetProperty("ssoEnabled").GetBoolean());

        var users = JsonDocument.Parse(lines[^2]).RootElement;
        Assert.Equal(2, users.GetArrayLength());
        var zed = users.EnumerateArray().Single(u => u.GetProperty("userName").GetString() == "zed");
        Assert.Equal("Zed Z", zed.GetProperty("displayName").GetString());
        Assert.Equal("Local", zed.GetProperty("kind").GetString());
        Assert.Equal("Admin", zed.GetProperty("role").GetString());
        Assert.True(zed.GetProperty("isBreakGlass").GetBoolean());
        Assert.False(zed.GetProperty("isDisabled").GetBoolean());
        Assert.False(zed.GetProperty("totpEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, zed.GetProperty("lastSignInAt").ValueKind);

        var human = await RunAsync(cs, ["list"]);
        Assert.Equal(0, human.Exit);
        Assert.Contains("zed", human.Out);
        Assert.DoesNotContain("#config", human.Out);
    }

    [Fact]
    public async Task Unknown_command_is_usage_error()
    {
        var cs = await MigratedDbAsync();
        foreach (var args in new[] { new[] { "frobnicate" }, [], ["create-user"], ["set-role", "x"], ["disable"], ["create-user", "a", "--admin", "--analyst"], ["create-user", "a", "--bogus"] })
        {
            var r = await RunAsync(cs, args);
            Assert.Equal(64, r.Exit);
            Assert.Contains("usage:", r.Err);
        }
    }

    [Fact]
    public async Task Missing_connection_string_is_a_configuration_error()
    {
        var r = await RunAsync("", ["list"], config: new ConfigurationBuilder().Build());
        Assert.Equal(78, r.Exit);
    }

    [Fact]
    public async Task Cli_actions_are_audited_without_user()
    {
        var cs = await MigratedDbAsync();
        Assert.Equal(0, (await RunAsync(cs, ["create-user", "gina", "--admin", "--password-stdin"], Password)).Exit);
        Assert.Equal(0, (await RunAsync(cs, ["create-user", "gina2", "--admin", "--password-stdin"], Password)).Exit);
        Assert.Equal(0, (await RunAsync(cs, ["reset-password", "gina", "--reset-totp", "--password-stdin"], "a brand new password")).Exit);
        Assert.Equal(0, (await RunAsync(cs, ["set-role", "gina", "analyst"])).Exit);

        await using var db = sql.CreateContext(cs);
        var rows = await db.AuditLogs.AsNoTracking().OrderBy(a => a.Id).ToListAsync(Ct);
        Assert.Contains(rows, a => a.Action == "user.create" && a.Target == "gina");
        Assert.Contains(rows, a => a.Action == "user.reset_password" && a.Target == "gina");
        Assert.Contains(rows, a => a.Action == "user.update" && a.Target == "gina");
        Assert.All(rows, a =>
        {
            Assert.Null(a.UserId);
            Assert.StartsWith("via maldex-admin: ", a.Details);
        });
        Assert.Contains(rows, a => a.Action == "user.create" && a.Details!.StartsWith("via maldex-admin: create-user gina --admin"));
        Assert.DoesNotContain(rows, a => a.Details!.Contains(Password));
    }

    [Fact]
    public async Task Admin_command_migrates_an_empty_database()
    {
        var cs = sql.CreateDatabaseConnectionString(); // nothing created, nothing migrated
        var r = await RunAsync(cs, ["create-user", "first", "--admin", "--password-stdin"], Password + "\n");
        Assert.True(r.Exit == 0, r.Out + r.Err);
        Assert.Equal(UserRole.Admin, (await UserAsync(cs, "first")).Role);
    }
}

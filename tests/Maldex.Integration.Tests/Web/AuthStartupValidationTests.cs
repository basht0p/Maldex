using Maldex.Web.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Maldex.Integration.Tests.Web;

public sealed class AuthStartupValidationTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Maldex.Web";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();

    [Fact]
    public void Entra_mode_is_a_config_error()
    {
        var problem = AuthStartupValidation.Problem(Config(("Maldex:Auth:Mode", "Entra")), new Env("Production"));
        Assert.NotNull(problem);
        Assert.Contains("Maldex:Auth:Mode 'Entra' is not supported", problem);
        Assert.Equal("Maldex:Auth:Mode 'Entra' is not supported; use Local (see README \"Single sign-on (optional)\")", problem);
        Assert.NotNull(AuthStartupValidation.Problem(Config(("Maldex:Auth:Mode", "Entra")), new Env("Development")));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Dev_mode_outside_development_is_an_error(string environment)
    {
        var problem = AuthStartupValidation.Problem(Config(("Maldex:Auth:Mode", "Dev")), new Env(environment));
        Assert.NotNull(problem);
        Assert.Contains($"Dev auth is not allowed in {environment}", problem);
        Assert.Null(AuthStartupValidation.Problem(Config(("Maldex:Auth:Mode", "Dev")), new Env("Development")));
        Assert.Null(AuthStartupValidation.Problem(Config(("Maldex:Auth:Mode", "dev")), new Env("Testing")));
    }

    [Fact]
    public void Password_login_off_without_sso_is_an_error()
    {
        var problem = AuthStartupValidation.Problem(Config(("Maldex:Auth:PasswordLogin", "false")), new Env("Production"));
        Assert.Equal("Maldex:Auth:PasswordLogin is false but Maldex:Auth:Oidc is not enabled; nobody could sign in", problem);
        Assert.Null(AuthStartupValidation.Problem(Config(("Maldex:Auth:PasswordLogin", "false"), ("Maldex:Auth:Oidc:Enabled", "true"),
            ("Maldex:Auth:Oidc:Authority", "https://idp.example.test/"), ("Maldex:Auth:Oidc:ClientId", "maldex")), new Env("Production")));
    }

    [Fact]
    public void Local_defaults_are_valid()
    {
        Assert.Null(AuthStartupValidation.Problem(Config(), new Env("Production")));
        Assert.Null(AuthStartupValidation.Problem(Config(("Maldex:Auth:Mode", "local")), new Env("Production")));
    }

    [Fact]
    public void Options_bind_with_the_documented_defaults()
    {
        var defaults = MaldexAuthOptions.Read(Config());
        Assert.Equal(["openid", "profile", "email"], defaults.Oidc.Scopes);
        Assert.Equal(TimeSpan.FromMinutes(5), defaults.StampValidationInterval);

        // A configured list replaces the default scopes (the plain binder would append to them).
        var custom = MaldexAuthOptions.Read(Config(("Maldex:Auth:Oidc:Scopes:0", "openid"), ("Maldex:Auth:Oidc:Scopes:1", "groups"),
            ("Maldex:Auth:Oidc:AdminValues:0", "maldex-admins"), ("Maldex:Auth:PasswordLogin", "false")));
        Assert.Equal(["openid", "groups"], custom.Oidc.Scopes);
        Assert.Equal(["maldex-admins"], custom.Oidc.AdminValues);
        Assert.False(custom.PasswordLogin);
    }

    /// <summary>A valid enabled OIDC configuration, with <paramref name="overrides"/> replacing or adding keys.</summary>
    private static (string Key, string Value)[] Oidc(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string>
        {
            ["Maldex:Auth:Oidc:Enabled"] = "true",
            ["Maldex:Auth:Oidc:Authority"] = "https://idp.example.test/",
            ["Maldex:Auth:Oidc:ClientId"] = "maldex",
        };
        foreach (var (k, v) in overrides) values[k] = v;
        return values.Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    [Fact]
    public void Oidc_requires_https_authority_outside_development()
    {
        Assert.Null(AuthStartupValidation.Problem(Config(Oidc()), new Env("Production")));

        foreach (var authority in new[] { "http://idp.example.test/", "http://localhost:5001", "http://127.0.0.1:8080", "", "idp.example.test", "/relative", "ftp://idp.example.test" })
        {
            var problem = AuthStartupValidation.Problem(Config(Oidc(("Maldex:Auth:Oidc:Authority", authority))), new Env("Production"));
            Assert.NotNull(problem);
            Assert.Contains("Maldex:Auth:Oidc:Authority", problem);
        }

        // Loopback over plain HTTP is allowed only in Development and Testing; any other http host never is.
        foreach (var environment in new[] { "Development", "Testing" })
        {
            Assert.Null(AuthStartupValidation.Problem(Config(Oidc(("Maldex:Auth:Oidc:Authority", "http://localhost:5001"))), new Env(environment)));
            Assert.Null(AuthStartupValidation.Problem(Config(Oidc(("Maldex:Auth:Oidc:Authority", "http://127.0.0.1:8080/realms/x"))), new Env(environment)));
            Assert.NotNull(AuthStartupValidation.Problem(Config(Oidc(("Maldex:Auth:Oidc:Authority", "http://idp.example.test"))), new Env(environment)));
        }
        Assert.NotNull(AuthStartupValidation.Problem(Config(Oidc(("Maldex:Auth:Oidc:Authority", "http://localhost:5001"))), new Env("Staging")));

        // Disabled SSO is not checked.
        Assert.Null(AuthStartupValidation.Problem(Config(("Maldex:Auth:Oidc:Enabled", "false"), ("Maldex:Auth:Oidc:Authority", "http://x")), new Env("Production")));
    }

    [Fact]
    public void Oidc_requires_client_id()
    {
        var problem = AuthStartupValidation.Problem(Config(Oidc(("Maldex:Auth:Oidc:ClientId", " "))), new Env("Production"));
        Assert.NotNull(problem);
        Assert.Contains("Maldex:Auth:Oidc:ClientId", problem);
    }

    [Fact]
    public void Role_claim_requires_admin_values()
    {
        var problem = AuthStartupValidation.Problem(Config(Oidc(("Maldex:Auth:Oidc:RoleClaim", "groups"), ("Maldex:Auth:Oidc:AnalystValues:0", "g-ana"))), new Env("Production"));
        Assert.NotNull(problem);
        Assert.Contains("Maldex:Auth:Oidc:AdminValues", problem);
        Assert.Null(AuthStartupValidation.Problem(Config(Oidc(("Maldex:Auth:Oidc:RoleClaim", "groups"), ("Maldex:Auth:Oidc:AdminValues:0", "g-admin"))), new Env("Production")));
    }
}

using System.Diagnostics;
using System.Reflection;
using Maldex.Infrastructure.Agent;
using Maldex.Infrastructure.Libvirt;
using Maldex.Infrastructure.Media;
using Maldex.Infrastructure.Net;
using Maldex.Infrastructure.Real;
using Maldex.Infrastructure.Scanning;
using Maldex.Infrastructure.Storage;
using Maldex.Orchestrator.Templates;
using Maldex.Orchestrator.Templates.Build;
using Maldex.Web;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Maldex.Integration.Tests.Web;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class PackagingConfigTests
{
    private static readonly string WebDll = Path.Combine(AppContext.BaseDirectory, "Maldex.Web.dll");

    private static IConfigurationRoot PackagedConfig() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json")
        .AddJsonFile("appsettings.Real.json")
        .AddJsonFile("appsettings.Package.json")
        .Build();

    private static readonly string[] AllowedAbsolute =
        ["/opt/maldex/", "/var/lib/maldex", "/var/log/maldex/", "/etc/maldex/", "/usr/share/", "/var/lib/inetsim"];

    [Fact]
    public void Package_config_points_every_path_option_at_opt_or_under_real_root()
    {
        var cfg = PackagedConfig();
        object Bind(Type t, string section) => cfg.GetSection(section).Get(t) ?? Activator.CreateInstance(t)!;
        var options = new (object Value, string Section)[]
        {
            (Bind(typeof(MediaOptions), MediaOptions.SectionName), MediaOptions.SectionName),
            (Bind(typeof(ArtifactStoreOptions), ArtifactStoreOptions.SectionName), ArtifactStoreOptions.SectionName),
            (Bind(typeof(LibvirtOptions), LibvirtOptions.SectionName), LibvirtOptions.SectionName),
            (Bind(typeof(NetOptions), NetOptions.SectionName), NetOptions.SectionName),
            (Bind(typeof(AgentChannelOptions), AgentChannelOptions.SectionName), AgentChannelOptions.SectionName),
            (Bind(typeof(TemplateJobOptions), TemplateJobOptions.SectionName), TemplateJobOptions.SectionName),
            (Bind(typeof(BuildOptions), BuildOptions.SectionName), BuildOptions.SectionName),
            (Bind(typeof(SuricataOptions), SuricataOptions.SectionName), SuricataOptions.SectionName),
            (Bind(typeof(YaraOptions), YaraOptions.SectionName), YaraOptions.SectionName),
            (Bind(typeof(RealOptions), RealOptions.SectionName), RealOptions.SectionName),
        };

        var checkedCount = 0;
        foreach (var (value, section) in options)
            foreach (var p in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.PropertyType != typeof(string) || !p.CanRead) continue;
                if (!(p.Name.EndsWith("Path") || p.Name.EndsWith("Dir") || p.Name.EndsWith("Root") || p.Name.EndsWith("Index"))) continue;
                var v = (string?)p.GetValue(value);
                if (string.IsNullOrEmpty(v)) continue;
                checkedCount++;
                var name = $"{section}:{p.Name}";
                if (Path.IsPathRooted(v))
                    Assert.True(AllowedAbsolute.Any(a => v == a.TrimEnd('/') || v.StartsWith(a, StringComparison.Ordinal)), $"{name} = '{v}' is absolute but outside the packaged layout");
                else
                {
                    Assert.False(v.StartsWith("deploy/") || v.StartsWith("rules/"), $"{name} = '{v}' is a bundled asset and must be an absolute /opt/maldex path");
                    Assert.False(v.Contains(".."), $"{name} = '{v}' escapes the root");
                }
            }
        Assert.True(checkedCount > 15, $"only {checkedCount} path properties were checked");

        Assert.Equal("/var/lib/maldex", cfg["Maldex:Real:Root"]);
        Assert.Equal("/opt/maldex/agent/Maldex.Agent.exe", cfg["Maldex:Templates:Jobs:AgentExePath"]);
        Assert.Equal("/opt/maldex/agent/Maldex.Agent.version", cfg["Maldex:Templates:Jobs:AgentVersionPath"]);
        Assert.StartsWith("/opt/maldex/deploy/", cfg["Maldex:Templates:Jobs:SysmonConfigPath"]);
        Assert.StartsWith("/opt/maldex/deploy/", cfg["Maldex:Templates:Build:AutounattendPath"]);
        Assert.StartsWith("/opt/maldex/deploy/", cfg["Maldex:Templates:Build:ProvisionScriptPath"]);
        Assert.StartsWith("/opt/maldex/deploy/", cfg["Maldex:Templates:Build:OfficeConfigPath"]);
        Assert.Equal("/opt/maldex/deploy/net", cfg["Maldex:Real:Net:DeployNetDir"]);
        Assert.StartsWith("/opt/maldex/deploy/net/suricata/", cfg["Maldex:Real:Suricata:ConfigPath"]);
        Assert.StartsWith("/opt/maldex/deploy/net/suricata/", cfg["Maldex:Real:Suricata:RulesPath"]);
        Assert.Equal("Real", cfg["Maldex:Mode"]);
        Assert.StartsWith("/opt/maldex/rules/", cfg["Maldex:Yara:RulesIndex"]);
    }

    [Fact]
    public void Extra_config_files_load_in_order_and_missing_ones_are_ignored()
    {
        var dir = Directory.CreateTempSubdirectory("maldex-extra-").FullName;
        try
        {
            var a = Path.Combine(dir, "a.json");
            var b = Path.Combine(dir, "b.json");
            File.WriteAllText(a, """{"Maldex":{"X":"from-a","OnlyA":"a"}}""");
            File.WriteAllText(b, """{"Maldex":{"X":"from-b"}}""");
            var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Maldex:X"] = "base" });
            ExtraConfig.Add(builder.Sources, $"{a}:{Path.Combine(dir, "missing.json")}:{b}:");
            var cfg = builder.Build();
            Assert.Equal("from-b", cfg["Maldex:X"]);
            Assert.Equal("a", cfg["Maldex:OnlyA"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Config_layering_matches_Program_cs()
    {
        var dir = Directory.CreateTempSubdirectory("maldex-layer-").FullName;
        const string envKey = "Maldex__Real__Libvirt__MachineType";
        try
        {
            var pkg = Path.Combine(AppContext.BaseDirectory, "appsettings.Package.json");
            var prod = Path.Combine(dir, "appsettings.Production.json");
            File.WriteAllText(prod, """{"Maldex":{"Real":{"Root":"/srv/maldex"}}}""");
            IConfigurationRoot Build(string extra)
            {
                var b = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json").AddEnvironmentVariables();
                var at = ExtraConfig.Add(b.Sources, extra);
                Assert.True(at >= 0);
                Assert.Equal("Real", b.Build()["Maldex:Mode"]);
                RealSettings.InsertAt(b.Sources, new Microsoft.Extensions.FileProviders.PhysicalFileProvider(AppContext.BaseDirectory), at);
                return b.Build();
            }

            var cfg = Build(pkg);
            Assert.Equal("Real", cfg["Maldex:Mode"]);
            Assert.Equal("q35", cfg["Maldex:Real:Libvirt:MachineType"]);           // only Real.json sets it
            Assert.Equal("/var/lib/maldex", cfg["Maldex:Real:Root"]);              // Package.json over Real.json ("")
            Assert.Equal("/opt/maldex/deploy/net", cfg["Maldex:Real:Net:DeployNetDir"]);

            cfg = Build($"{pkg}:{prod}");
            Assert.Equal("/srv/maldex", cfg["Maldex:Real:Root"]);                  // Production.json over Package.json

            Environment.SetEnvironmentVariable(envKey, "env-wins");
            try { Assert.Equal("env-wins", Build($"{pkg}:{prod}")["Maldex:Real:Libvirt:MachineType"]); }
            finally { Environment.SetEnvironmentVariable(envKey, null); }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Version_flag_prints_version_and_exits_zero()
    {
        var (code, stdout, _) = await RunWeb(["--version"], new() { ["ASPNETCORE_ENVIRONMENT"] = "Production" });
        Assert.Equal(0, code);
        Assert.Matches(@"^\d+\.\d+\.\d+", stdout.Trim());
    }

    [Fact]
    public async Task Production_without_connection_string_fails_fast_with_hint()
    {
        var (code, stdout, stderr) = await RunWeb([], new() { ["ASPNETCORE_ENVIRONMENT"] = "Production", ["ASPNETCORE_URLS"] = "http://127.0.0.1:0" });
        Assert.Equal(StartupChecks.ConfigErrorExitCode, code);
        Assert.Equal(78, code);   // the packaged unit's RestartPreventExitStatus=78
        Assert.Contains("ConnectionStrings:Maldex is not set; edit /etc/maldex/appsettings.Production.json and restart maldex", stdout + stderr);
    }

    // The packaged unit: WorkingDirectory=/var/lib/maldex plus these environment lines (packaging/systemd/maldex.service).
    private static Dictionary<string, string> UnitEnvironment(string appDir) => new()
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Production",
        ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
        ["ASPNETCORE_CONTENTROOT"] = appDir,
        ["MALDEX_EXTRA_CONFIG"] = Path.Combine(appDir, "appsettings.Package.json"),
    };

    private static string[] Lines(string stdout) =>
        stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimEnd(Path.DirectorySeparatorChar)).ToArray();

    [Fact]
    public async Task Unit_environment_roots_content_and_config_in_the_app_dir_whatever_the_cwd()
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var cwd = Directory.CreateTempSubdirectory("maldex-cwd-").FullName;
        // The SPA is in the publish output (build.sh package checks wwwroot/index.html); the test output has it only after ./build.sh build.
        var wwwroot = Path.Combine(appDir, "wwwroot");
        var index = Path.Combine(wwwroot, "index.html");
        var createdDir = !Directory.Exists(wwwroot);
        var createdIndex = !File.Exists(index);
        if (createdIndex) { Directory.CreateDirectory(wwwroot); File.WriteAllText(index, "<!doctype html>"); }
        try
        {
            var (code, stdout, stderr) = await RunWeb(["--print-paths"], UnitEnvironment(appDir), cwd);
            Assert.True(code == 0, stderr);
            var lines = Lines(stdout);
            Assert.Contains($"ContentRoot={appDir}", lines);
            Assert.Contains($"WebRoot={wwwroot}", lines);
            Assert.Contains("IndexHtml=present", lines);
            Assert.Contains($"Config={Path.Combine(appDir, "appsettings.json")}", lines);
            Assert.Contains($"Config={Path.Combine(appDir, "appsettings.Real.json")}", lines);   // Package.json sets Mode=Real
            Assert.Contains($"Config={Path.Combine(appDir, "appsettings.Package.json")}", lines);
            Assert.DoesNotContain(lines, l => l.StartsWith("Config=" + cwd, StringComparison.Ordinal));

            // Without ASPNETCORE_CONTENTROOT the content root is the cwd and the app's settings files and SPA are not found.
            var env = UnitEnvironment(appDir);
            env.Remove("ASPNETCORE_CONTENTROOT");
            (code, stdout, stderr) = await RunWeb(["--print-paths"], env, cwd);
            Assert.True(code == 0, stderr);
            lines = Lines(stdout);
            Assert.Contains($"ContentRoot={cwd}", lines);
            Assert.Contains("IndexHtml=missing", lines);
            Assert.DoesNotContain($"Config={Path.Combine(appDir, "appsettings.Real.json")}", lines);
        }
        finally
        {
            Directory.Delete(cwd, true);
            if (createdDir) Directory.Delete(wwwroot, true);
            else if (createdIndex) File.Delete(index);
        }
    }

    [Fact]
    public async Task Production_real_mode_with_an_unusable_root_fails_fast_with_EX_CONFIG()
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var dir = Directory.CreateTempSubdirectory("maldex-root-").FullName;
        try
        {
            var env = UnitEnvironment(appDir);
            // A closed port: the process must exit on the Root check, before it ever connects.
            env["ConnectionStrings__Maldex"] = "Server=127.0.0.1,1;Database=maldex;User Id=x;Password=x;TrustServerCertificate=True;Connect Timeout=1";
            env["Maldex__Real__Root"] = Path.Combine(dir, "missing");
            var (code, stdout, stderr) = await RunWeb([], env, dir);
            Assert.Equal(StartupChecks.ConfigErrorExitCode, code);
            Assert.Contains("Maldex:Real:Root", stdout + stderr);
            Assert.Contains("does not exist", stdout + stderr);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Real_root_problem_covers_unset_relative_missing_and_unwritable()
    {
        Assert.Contains("is not set", StartupChecks.RealRootProblem(null));
        Assert.Contains("is not set", StartupChecks.RealRootProblem("  "));
        Assert.Contains("absolute", StartupChecks.RealRootProblem("var/lib/maldex"));
        var dir = Directory.CreateTempSubdirectory("maldex-root-").FullName;
        try
        {
            Assert.Contains("does not exist", StartupChecks.RealRootProblem(Path.Combine(dir, "missing")));
            Assert.Null(StartupChecks.RealRootProblem(dir));
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));   // the write probe leaves nothing behind
            if (Environment.UserName != "root")   // root writes through any mode
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                Assert.Contains("is not writable", StartupChecks.RealRootProblem(dir));
            }
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(dir, true);
        }
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunWeb(string[] args, Dictionary<string, string> env, string? cwd = null)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd ?? AppContext.BaseDirectory };
        psi.ArgumentList.Add(WebDll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var k in new[] { "ConnectionStrings__Maldex", "MALDEX_EXTRA_CONFIG", "Maldex__Mode", "DOTNET_ENVIRONMENT", "ASPNETCORE_CONTENTROOT", "Maldex__Real__Root" }) psi.Environment.Remove(k);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { p.Kill(true); throw new Xunit.Sdk.XunitException("Maldex.Web did not exit within 60 s"); }
        return (p.ExitCode, await so, await se);
    }
}

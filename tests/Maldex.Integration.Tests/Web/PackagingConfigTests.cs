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
        Assert.NotEqual(0, code);
        Assert.Contains("ConnectionStrings:Maldex is not set; edit /etc/maldex/appsettings.Production.json and restart maldex", stdout + stderr);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunWeb(string[] args, Dictionary<string, string> env)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
        psi.ArgumentList.Add(WebDll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var k in new[] { "ConnectionStrings__Maldex", "MALDEX_EXTRA_CONFIG", "Maldex__Mode", "DOTNET_ENVIRONMENT" }) psi.Environment.Remove(k);
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

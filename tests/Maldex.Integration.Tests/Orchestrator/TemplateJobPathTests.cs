using Maldex.Infrastructure.Real;
using Maldex.Orchestrator;
using Maldex.Orchestrator.Templates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

/// <summary>The job component paths are relative to the repo root, not to the process working directory (the web host starts in src/Maldex.Web).</summary>
public sealed class TemplateJobPathTests
{
    private static TemplateJobOptions Resolve(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddMaldexOrchestrator(config);
        using var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IOptions<TemplateJobOptions>>().Value;
    }

    [Fact]
    public void DefaultPaths_ResolveAgainstTheRepoRoot_NotTheWorkingDirectory()
    {
        var root = RealPaths.FindRoot(AppContext.BaseDirectory);
        var o = Resolve(new());
        Assert.Equal(Path.Combine(root, ".data", "agent", "Maldex.Agent.exe"), o.AgentExePath);
        Assert.Equal(Path.Combine(root, ".data", "agent", "Maldex.Agent.version"), o.AgentVersionPath);
        Assert.Equal(Path.Combine(root, "deploy", "guest", "provision", "sysmon-config.xml"), o.SysmonConfigPath);
        Assert.Equal(Path.Combine(root, ".data", "mitm", "ca", "mitmproxy-ca-cert.pem"), o.MitmCaPemPath);
    }

    [Fact]
    public void ConfiguredRealRoot_IsTheBase()
    {
        var root = Path.Combine(Path.GetTempPath(), "maldex-root-" + Guid.NewGuid().ToString("N"));
        var o = Resolve(new() { ["Maldex:Real:Root"] = root });
        Assert.Equal(Path.Combine(root, ".data", "agent", "Maldex.Agent.exe"), o.AgentExePath);
        Assert.Equal(Path.Combine(root, ".data", "mitm", "ca", "mitmproxy-ca-cert.pem"), o.MitmCaPemPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Fake")]
    public void FakeMode_TakesOneDefenderReading(string? mode)
    {
        // The fake guest's Defender never changes, so a minute of readings per check is only real time lost.
        var o = Resolve(new() { ["Maldex:Mode"] = mode });
        Assert.Equal(TimeSpan.Zero, o.DefenderSettleWindow);
    }

    [Fact]
    public void FakeMode_AConfiguredSettleWindow_IsKept()
    {
        var o = Resolve(new() { ["Maldex:Mode"] = "Fake", [$"{TemplateJobOptions.SectionName}:DefenderSettleWindow"] = "00:01:00" });
        Assert.Equal(TimeSpan.FromMinutes(1), o.DefenderSettleWindow);
    }

    [Fact]
    public void RealMode_KeepsTheDefenderSettleWindow()
    {
        var o = Resolve(new() { ["Maldex:Mode"] = "Real" });
        Assert.Equal(TimeSpan.FromSeconds(60), o.DefenderSettleWindow);
    }

    [Fact]
    public void AbsolutePaths_AreLeftUnchanged()
    {
        var abs = Path.Combine(Path.GetTempPath(), "elsewhere", "agent.exe");
        var o = Resolve(new()
        {
            [$"{TemplateJobOptions.SectionName}:AgentExePath"] = abs,
            [$"{TemplateJobOptions.SectionName}:SysmonConfigPath"] = "custom/sysmon.xml",
        });
        Assert.Equal(abs, o.AgentExePath);
        Assert.Equal(Path.Combine(RealPaths.FindRoot(AppContext.BaseDirectory), "custom", "sysmon.xml"), o.SysmonConfigPath);
    }
}

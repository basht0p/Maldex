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

    [Theory]
    [InlineData("00:00:00", "5", "8", "DefenderSettleWindow is 0 s; it must be at least 30 s (0 only in fake mode)")]
    [InlineData("00:00:20", "5", "3", "DefenderSettleWindow is 20 s; it must be at least 30 s (0 only in fake mode)")]
    [InlineData("00:01:00", "5", "2", "DefenderSettleMinReadings is 2; it must be at least 3")]
    [InlineData("00:01:00", "5", "14", "DefenderSettleMinReadings is 14, but a 60 s window read every 5 s holds at most 13 readings")]
    [InlineData("00:01:00", "0", "8", "DefenderSettleInterval is 0 s; it must be positive")]
    public void RealMode_RejectsADefenderSettleWindowThatCannotWork(string window, string intervalSeconds, string minReadings, string problem)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new()
        {
            ["Maldex:Mode"] = "Real",
            [$"{TemplateJobOptions.SectionName}:DefenderSettleWindow"] = window,
            [$"{TemplateJobOptions.SectionName}:DefenderSettleInterval"] = TimeSpan.FromSeconds(int.Parse(intervalSeconds)).ToString(),
            [$"{TemplateJobOptions.SectionName}:DefenderSettleMinReadings"] = minReadings,
        }));
        Assert.Equal($"{TemplateJobOptions.SectionName}:{problem}", ex.Message);
    }

    [Fact]
    public void RealMode_AcceptsAMinimumThatFillsEverySlot()
    {
        var o = Resolve(new() { ["Maldex:Mode"] = "Real", [$"{TemplateJobOptions.SectionName}:DefenderSettleMinReadings"] = "13" });
        Assert.Equal(13, o.DefenderSettleMinReadings);
    }

    [Fact]
    public void FakeMode_AConfiguredSettleWindow_IsValidatedToo()
    {
        Assert.Throws<OptionsValidationException>(() => Resolve(new()
        {
            ["Maldex:Mode"] = "Fake", [$"{TemplateJobOptions.SectionName}:DefenderSettleWindow"] = "00:00:10",
        }));
    }

    [Fact]
    public void TheSettleOptions_AreValidatedAtStartup()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Maldex:Mode"] = "Real" }).Build();
        var services = new ServiceCollection();
        services.AddMaldexOrchestrator(config);
        // ValidateOnStart registers a startup check for these options (the host runs it before serving).
        using var sp = services.BuildServiceProvider();
        Assert.Contains(sp.GetServices<Microsoft.Extensions.Options.IValidateOptions<TemplateJobOptions>>(), v => v is not null);
        Assert.Contains(services, d => d.ServiceType.FullName == "Microsoft.Extensions.Options.IStartupValidator");
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

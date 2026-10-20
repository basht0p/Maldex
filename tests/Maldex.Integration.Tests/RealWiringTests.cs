using Maldex.Core.Abstractions;
using Maldex.Infrastructure;
using Maldex.Infrastructure.Agent;
using Maldex.Infrastructure.Execution;
using Maldex.Infrastructure.Fakes;
using Maldex.Infrastructure.Guacamole;
using Maldex.Infrastructure.Isolation;
using Maldex.Infrastructure.Libvirt;
using Maldex.Infrastructure.Net;
using Maldex.Infrastructure.Real;
using Maldex.Infrastructure.Scanning;
using Maldex.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Maldex.Integration.Tests;

public sealed class RealWiringTests
{
    private sealed class RecordingRunner : IProcessRunner
    {
        public List<string> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null, TimeSpan? timeout = null)
        {
            Calls.Add(file + " " + string.Join(' ', args));
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }

    private sealed class FixedAddresses(params string[] ips) : IHostAddressProvider
    {
        public IReadOnlyList<string> GetIPv4Addresses() => ips;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] extra)
    {
        var d = new Dictionary<string, string?> { ["Maldex:Mode"] = "Real" };
        foreach (var (k, v) in extra) d[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    private static ServiceDescriptor Addresses(params string[] ips) => ServiceDescriptor.Singleton<IHostAddressProvider>(new FixedAddresses(ips));

    private static ServiceProvider Build(IConfiguration config, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(config);
        services.AddMaldexInfrastructure(config);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void RealMode_ResolvesRealImplementations()
    {
        using var sp = Build(Config(), s => s.Replace(Addresses("192.168.1.10")));
        Assert.IsType<ProcessRunner>(sp.GetRequiredService<IProcessRunner>());
        Assert.IsType<LibvirtHypervisor>(sp.GetRequiredService<IHypervisor>());
        Assert.IsType<VncConsoleLocator>(sp.GetRequiredService<IVmConsoleLocator>());
        Assert.IsType<VirtioSerialAgentChannelFactory>(sp.GetRequiredService<IAgentChannelFactory>());
        Assert.IsType<NetnsEgressRouter>(sp.GetRequiredService<IEgressRouter>());
        Assert.IsType<CaptureService>(sp.GetRequiredService<ICaptureService>());
        Assert.IsType<SuricataRunner>(sp.GetRequiredService<ISuricataRunner>());
        Assert.IsType<LeaseFileRunNetworkInfo>(sp.GetRequiredService<IRunNetworkInfo>());
        Assert.IsType<WireGuardExpectedEgress>(sp.GetRequiredService<IExpectedEgress>());
        Assert.Equal("FileSystemArtifactStore", sp.GetRequiredService<IArtifactStore>().GetType().Name);
        Assert.IsType<YaraCliScanner>(sp.GetRequiredService<IYaraScanner>());
        Assert.NotNull(sp.GetRequiredService<GuacamoleTokenService>());
        Assert.True(sp.GetRequiredService<IOptions<SelfTestOptions>>().Value.RequireControlPlaneProbes);
        Assert.IsType<HostGuardProbe>(sp.GetRequiredService<IHostGuardProbe>());
    }

    [Fact]
    public void RealMode_ResolvingServicesStartsNothing()
    {
        var runner = new RecordingRunner();
        using var sp = Build(Config(), s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IProcessRunner>(runner));
            s.Replace(Addresses("192.168.1.10"));
        });
        _ = sp.GetRequiredService<IHypervisor>();
        _ = sp.GetRequiredService<IVmConsoleLocator>();
        _ = sp.GetRequiredService<IAgentChannelFactory>();
        _ = sp.GetRequiredService<IEgressRouter>();
        _ = sp.GetRequiredService<ICaptureService>();
        _ = sp.GetRequiredService<ISuricataRunner>();
        _ = sp.GetRequiredService<IRunNetworkInfo>();
        _ = sp.GetRequiredService<IExpectedEgress>();
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task FakeMode_StillDefault()
    {
        using var sp = Build(new ConfigurationBuilder().Build());
        Assert.IsType<FakeHypervisor>(sp.GetRequiredService<IHypervisor>());
        Assert.IsType<FakeAgentChannelFactory>(sp.GetRequiredService<IAgentChannelFactory>());
        Assert.IsType<NoopEgressRouter>(sp.GetRequiredService<IEgressRouter>());
        Assert.IsType<NoopCaptureService>(sp.GetRequiredService<ICaptureService>());
        Assert.IsType<NoopSuricataRunner>(sp.GetRequiredService<ISuricataRunner>());
        Assert.IsType<FakeConsoleLocator>(sp.GetRequiredService<IVmConsoleLocator>());
        Assert.IsType<FakeRunNetworkInfo>(sp.GetRequiredService<IRunNetworkInfo>());
        Assert.IsType<FakeExpectedEgress>(sp.GetRequiredService<IExpectedEgress>());
        Assert.False(sp.GetRequiredService<IOptions<SelfTestOptions>>().Value.RequireControlPlaneProbes);
        Assert.True(await sp.GetRequiredService<IWorkerStartGate>().AllowStartAsync(CancellationToken.None));
    }

    [Fact]
    public void RunDir_SharedAcrossOptions()
    {
        var root = Directory.CreateTempSubdirectory("maldex-root-").FullName;
        using var sp = Build(Config(("Maldex:Real:Root", root), ("Maldex:Real:RunDir", "vm/run-x"),
            ("Maldex:Real:Libvirt:RunDir", "other/a"), ("Maldex:Real:Agent:RunDir", "/elsewhere/b"), ("Maldex:Real:Net:RunDir", "other/c")),
            s => s.Replace(Addresses("192.168.1.10")));
        var expected = Path.Combine(root, "vm", "run-x");
        Assert.Equal(expected, sp.GetRequiredService<IOptions<LibvirtOptions>>().Value.RunDir);
        Assert.Equal(expected, sp.GetRequiredService<IOptionsMonitor<AgentChannelOptions>>().CurrentValue.RunDir);
        Assert.Equal(expected, sp.GetRequiredService<IOptions<NetOptions>>().Value.RunDir);
    }

    [Fact]
    public void RelativePaths_ResolvedAgainstRoot()
    {
        var root = Directory.CreateTempSubdirectory("maldex-root-").FullName;
        using var sp = Build(Config(("Maldex:Real:Root", root), ("Maldex:Real:Net:WireGuardConfigDir", "wg-conf")),
            s => s.Replace(Addresses("192.168.1.10")));
        var lv = sp.GetRequiredService<IOptions<LibvirtOptions>>().Value;
        var su = sp.GetRequiredService<IOptions<SuricataOptions>>().Value;
        var net = sp.GetRequiredService<IOptions<NetOptions>>().Value;
        Assert.Equal(Path.Combine(root, ".data", "vm", "base"), lv.BaseImageDir);
        Assert.Equal(Path.Combine(root, "deploy", "net", "suricata", "suricata.yaml"), su.ConfigPath);
        Assert.Equal(Path.Combine(root, "deploy", "net", "suricata", "maldex-dev.rules"), su.RulesPath);
        Assert.Equal(Path.Combine(root, ".data", "mitm", "ca"), net.MitmConfDir);
        Assert.Equal(Path.Combine(root, "deploy", "net"), net.DeployNetDir);
        Assert.Equal(Path.Combine(root, "wg-conf"), net.WireGuardConfigDir);
        Assert.Equal("/var/lib/inetsim", net.InetSimDataDir); // absolute stays
        Assert.Equal("suricata", su.Binary); // bare names stay on PATH
    }

    [Fact]
    public void DefaultRoot_IsRepositoryRoot_AndMissingRootFailsClearly()
    {
        var found = RealPaths.FindRoot(AppContext.BaseDirectory);
        Assert.True(File.Exists(Path.Combine(found, "Maldex.slnx")));
        var empty = Directory.CreateTempSubdirectory("maldex-noroot-").FullName;
        var ex = Assert.Throws<InvalidOperationException>(() => RealPaths.FindRoot(empty));
        Assert.Contains("Maldex.slnx", ex.Message);
        Assert.Contains("Maldex:Real:Root", ex.Message);
    }

    [Fact]
    public void ControlPlaneIps_AutoDetectedNonEmpty()
    {
        // The selection rule: no loopback, link-local, mx* links or IPv6; docker bridges kept.
        var picked = NetworkInterfaceHostAddressProvider.Filter(
        [
            ("lo", true, System.Net.IPAddress.Parse("127.0.0.1")),
            ("eth0", false, System.Net.IPAddress.Parse("192.168.1.10")),
            ("eth0", false, System.Net.IPAddress.Parse("169.254.3.4")),
            ("eth0", false, System.Net.IPAddress.Parse("fe80::1")),
            ("docker0", false, System.Net.IPAddress.Parse("172.17.0.1")),
            ("mxb0123456789ab", false, System.Net.IPAddress.Parse("10.213.0.1")),
        ]);
        Assert.Equal(["172.17.0.1", "192.168.1.10"], picked);

        // Union with configured values, de-duplicated.
        using var merged = Build(Config(("Maldex:Real:Net:ControlPlaneIps:0", "203.0.113.7"), ("Maldex:Real:Net:ControlPlaneIps:1", "192.168.1.10")),
            s => s.Replace(Addresses("192.168.1.10", "172.17.0.1")));
        Assert.Equal(["172.17.0.1", "192.168.1.10", "203.0.113.7"], merged.GetRequiredService<IOptions<NetOptions>>().Value.ControlPlaneIps.Order().ToArray());

        // Nothing configured and nothing detected: refuse.
        using var empty = Build(Config(), s => s.Replace(Addresses()));
        var ex = Assert.Throws<OptionsValidationException>(() => empty.GetRequiredService<IOptions<NetOptions>>().Value);
        Assert.Contains("ControlPlaneIps", ex.Message);
    }

    private sealed class FailingProbe : IHostGuardProbe
    {
        public Task<IReadOnlyList<GuardResult>> CheckAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GuardResult>>([new GuardResult("host guard table", GuardSeverity.Fail, "nft table missing")]);
    }

    private sealed class PassingProbe : IHostGuardProbe
    {
        public Task<IReadOnlyList<GuardResult>> CheckAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GuardResult>>([new GuardResult("base image", GuardSeverity.Warn, "missing"), new GuardResult("kvm", GuardSeverity.Ok, "")]);
    }

    private sealed class SpyWorker : IHostedService
    {
        public bool Started, Stopped;
        public Task StartAsync(CancellationToken ct) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { Stopped = true; return Task.CompletedTask; }
    }

    private sealed class LogSink : ILoggerProvider, ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (Entries) Entries.Add((logLevel, formatter(state, exception))); }
    }

    private static async Task<(SpyWorker Worker, LogSink Log)> RunHostAsync(IHostGuardProbe probe)
    {
        var worker = new SpyWorker();
        var sink = new LogSink();
        var b = Host.CreateApplicationBuilder();
        b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Maldex:Mode"] = "Real" });
        b.Logging.ClearProviders();
        b.Logging.AddProvider(sink);
        b.Services.AddMaldexInfrastructure(b.Configuration);
        b.Services.Replace(Addresses("192.168.1.10"));
        b.Services.Replace(ServiceDescriptor.Singleton(probe));
        b.Services.AddSingleton(worker);
        b.Services.AddGatedHostedService<SpyWorker>();
        using var host = b.Build();
        await host.StartAsync();
        await host.Services.GetRequiredService<IWorkerStartGate>().AllowStartAsync(CancellationToken.None); // the gated start runs in the background
        await Task.Delay(100);
        await host.StopAsync();
        return (worker, sink);
    }

    [Fact]
    public async Task Guards_FailClosed_WorkerNotStarted()
    {
        var (worker, log) = await RunHostAsync(new FailingProbe());
        Assert.False(worker.Started);
        Assert.False(worker.Stopped);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("nft table missing"));
    }

    [Fact]
    public async Task Guards_WarningsOnly_WorkerStarts()
    {
        var (worker, log) = await RunHostAsync(new PassingProbe());
        Assert.True(worker.Started);
        Assert.True(worker.Stopped);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("base image"));
    }

    private sealed class FailingNftRunner : IProcessRunner
    {
        public List<string> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct, string? stdin = null, TimeSpan? timeout = null)
        {
            var line = file + " " + string.Join(' ', args);
            Calls.Add(line);
            return Task.FromResult(line.Contains("nft") ? new ProcessResult(1, "", "No such file") : new ProcessResult(0, "", ""));
        }
    }

    [Fact]
    public async Task Real_json_overrides_appsettings_but_environment_overrides_real_json()
    {
        var dir = Directory.CreateTempSubdirectory("maldex-cfg-").FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "appsettings.json"), "{\"Maldex\":{\"Mode\":\"Fake\",\"Probe\":\"base\",\"EnvWins\":\"base\"}}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "appsettings.Real.json"), "{\"Maldex\":{\"Mode\":\"Real\",\"Probe\":\"real\",\"EnvWins\":\"real\"}}", TestContext.Current.CancellationToken);
        Environment.SetEnvironmentVariable("Maldex__EnvWins", "env");
        try
        {
            var fp = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(dir);
            var cb = new ConfigurationBuilder();
            cb.AddJsonFile(fp, "appsettings.json", optional: false, reloadOnChange: false);
            cb.AddEnvironmentVariables("ASPNETCORE_");
            cb.AddEnvironmentVariables();
            Maldex.Web.RealSettings.Insert(cb.Sources, fp);
            var c = cb.Build();
            Assert.Equal("Real", c["Maldex:Mode"]);
            Assert.Equal("real", c["Maldex:Probe"]);
            Assert.Equal("env", c["Maldex:EnvWins"]);
        }
        finally { Environment.SetEnvironmentVariable("Maldex__EnvWins", null); }
    }

    [Fact]
    public void RelativeRoot_IsRejected() =>
        Assert.Contains("absolute", Assert.Throws<InvalidOperationException>(() => Build(Config(("Maldex:Real:Root", "relative/dir")))).Message);

    [Fact]
    public async Task HostGuardProbe_FailsWhenNftTablesMissing_WarnsOnMissingImage()
    {
        var runner = new FailingNftRunner();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Maldex:Images:0"] = "no-such-image" }).Build();
        var probe = new HostGuardProbe(runner, Options.Create(new LibvirtOptions { BaseImageDir = Path.GetTempPath() }), config);
        var results = await probe.CheckAsync(CancellationToken.None);
        Assert.Contains(results, r => r.Severity == GuardSeverity.Fail && r.Name.Contains("inet"));
        Assert.Contains(results, r => r.Severity == GuardSeverity.Fail && r.Name.Contains("bridge"));
        Assert.Contains(runner.Calls, c => c == "sudo -n nft list table inet maldex_guard");
        Assert.Contains(runner.Calls, c => c == "sudo -n nft list table bridge maldex_guard");
        Assert.Contains(results, r => r.Name.Contains("base image") && r.Severity == GuardSeverity.Warn);
    }
}

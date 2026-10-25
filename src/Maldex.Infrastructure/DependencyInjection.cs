using Maldex.Core.Abstractions;
using Maldex.Infrastructure.Agent;
using Maldex.Infrastructure.Execution;
using Maldex.Infrastructure.Libvirt;
using Maldex.Infrastructure.Media;
using Maldex.Infrastructure.Real;
using Maldex.Infrastructure.Fakes;
using Maldex.Infrastructure.Guacamole;
using Maldex.Infrastructure.Isolation;
using Maldex.Infrastructure.Net;
using Maldex.Infrastructure.Scanning;
using Maldex.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maldex.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddMaldexInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var mode = configuration["Maldex:Mode"];
        if (string.IsNullOrEmpty(mode)) mode = "Fake";

        services.TryAddSingleton(TimeProvider.System);
        services.Configure<ArtifactStoreOptions>(configuration.GetSection(ArtifactStoreOptions.SectionName));
        services.Configure<ReplayOptions>(configuration.GetSection(ReplayOptions.SectionName));
        services.Configure<FakeHypervisorOptions>(configuration.GetSection(FakeHypervisorOptions.SectionName));
        services.Configure<FakeEgressOptions>(configuration.GetSection(FakeEgressOptions.SectionName));
        services.Configure<YaraOptions>(configuration.GetSection(YaraOptions.SectionName));
        services.Configure<GuacamoleOptions>(configuration.GetSection(GuacamoleOptions.SectionName));
        services.AddSingleton<GuacamoleTokenService>();
        services.Configure<NetOptions>(configuration.GetSection(NetOptions.SectionName));
        services.Configure<SelfTestOptions>(configuration.GetSection(SelfTestOptions.SectionName));

        if (!OperatingSystem.IsWindows()) services.AddSingleton<IArtifactStore, FileSystemArtifactStore>();
        services.AddSingleton<IDiskSpaceProbe, DiskSpaceProbe>();
        services.Configure<MediaOptions>(configuration.GetSection(MediaOptions.SectionName));
        // Resolved against the same base as real mode, in both modes (the media root must not depend on the working directory).
        services.PostConfigure<MediaOptions>(o =>
        {
            var configuredRoot = configuration[$"{RealOptions.SectionName}:{nameof(RealOptions.Root)}"];
            var root = string.IsNullOrWhiteSpace(configuredRoot) ? RealPaths.FindRoot(AppContext.BaseDirectory) : Path.GetFullPath(configuredRoot);
            if (string.IsNullOrWhiteSpace(o.Root)) throw new InvalidOperationException("Maldex:Media:Root must not be empty.");
            o.Root = RealPaths.Resolve(root, o.Root);
        });
        services.AddSingleton<MediaStore>();
        // YARA is real in every mode; the yara binary is a host dependency, not a fake.
        services.AddSingleton<IYaraScanner, YaraCliScanner>();

        switch (mode)
        {
            case "Fake":
                services.AddSingleton<IHypervisor, FakeHypervisor>();
                services.AddSingleton<ITemplateStore, FakeTemplateStore>();
                services.AddSingleton<IGuestDiskEditor, FakeGuestDiskEditor>();
                services.AddSingleton<FakeGuest>();
                services.AddSingleton<IAgentChannelFactory, FakeAgentChannelFactory>();
                services.AddSingleton<IEgressRouter, NoopEgressRouter>();
                services.AddSingleton<ICaptureService, NoopCaptureService>();
                services.PostConfigure<SelfTestOptions>(o => o.RequireControlPlaneProbes = false); // fake mode has no host LAN to protect
                services.AddSingleton<IRunNetworkInfo, FakeRunNetworkInfo>();
                services.AddSingleton<IExpectedEgress, FakeExpectedEgress>();
                services.AddSingleton<ISuricataRunner, NoopSuricataRunner>();
                services.AddSingleton<IVmConsoleLocator, FakeConsoleLocator>();
                services.AddSingleton<IWorkerStartGate, OpenWorkerStartGate>();
                services.AddSingleton<IMediaInspector, FakeMediaInspector>();
                break;
            case "Real":
                AddReal(services, configuration);
                break;
            default:
                throw new InvalidOperationException($"Unknown Maldex:Mode '{mode}'. Expected 'Fake' or 'Real'.");
        }

        return services;
    }

    private static void AddReal(IServiceCollection services, IConfiguration configuration)
    {
        var realSection = configuration.GetSection(RealOptions.SectionName);
        var real = realSection.Get<RealOptions>() ?? new RealOptions();
        // One base for every relative path; a missing marker fails startup right here, with a clear message.
        if (!string.IsNullOrWhiteSpace(real.Root) && !Path.IsPathRooted(real.Root))
            throw new InvalidOperationException($"Maldex:Real:Root must be an absolute path (got '{real.Root}').");
        var root = string.IsNullOrWhiteSpace(real.Root) ? RealPaths.FindRoot(AppContext.BaseDirectory) : Path.GetFullPath(real.Root);
        var runDir = RealPaths.Resolve(root, real.RunDir);
        string P(string path) => RealPaths.Resolve(root, path);

        services.Configure<RealOptions>(realSection);
        services.Configure<LibvirtOptions>(configuration.GetSection(LibvirtOptions.SectionName));
        services.Configure<AgentChannelOptions>(configuration.GetSection(AgentChannelOptions.SectionName));
        services.Configure<SuricataOptions>(configuration.GetSection(SuricataOptions.SectionName));

        // ONE RunDir: the agent socket {RunDir}/{vm}/agent.sock must be the same path for hypervisor and channel.
        services.PostConfigure<LibvirtOptions>(o =>
        {
            o.RunDir = runDir;
            o.BaseImageDir = P(o.BaseImageDir);
            o.OvmfCodePath = P(o.OvmfCodePath);
        });
        services.PostConfigure<AgentChannelOptions>(o => o.RunDir = runDir);
        services.PostConfigure<SuricataOptions>(o =>
        {
            o.ConfigPath = P(o.ConfigPath);
            o.RulesPath = P(o.RulesPath);
        });

        services.TryAddSingleton<IHostAddressProvider, NetworkInterfaceHostAddressProvider>();
        services.AddOptions<NetOptions>()
            .PostConfigure<IHostAddressProvider>((o, hostAddresses) =>
            {
                o.RunDir = runDir;
                o.MitmConfDir = P(o.MitmConfDir);
                o.DeployNetDir = P(o.DeployNetDir);
                o.InetSimDataDir = P(o.InetSimDataDir);
                o.WireGuardConfigDir = P(o.WireGuardConfigDir);
                // The guest must not reach ANY host address: configured ones plus every detected non-loopback IPv4 (docker bridges included).
                o.ControlPlaneIps = o.ControlPlaneIps.Concat(hostAddresses.GetIPv4Addresses())
                    .Where(ip => !string.IsNullOrWhiteSpace(ip)).Select(ip => ip.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            })
            .Validate(o => o.ControlPlaneIps.Length > 0, "Maldex:Real:Net:ControlPlaneIps is empty and no host IPv4 address could be detected; refusing to start real mode without knowing which addresses guests must not reach.")
            .ValidateOnStart();

        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IHypervisor, LibvirtHypervisor>();
        services.AddSingleton<ITemplateStore, LibvirtTemplateStore>();
        services.AddSingleton<IGuestDiskEditor, GuestfishDiskEditor>();
        services.AddSingleton<IVmConsoleLocator, VncConsoleLocator>();
        services.AddSingleton<IAgentChannelFactory, VirtioSerialAgentChannelFactory>();
        services.AddSingleton<IEgressRouter, NetnsEgressRouter>();
        services.AddSingleton<ICaptureService, CaptureService>();
        services.AddSingleton<ISuricataRunner, SuricataRunner>();
        services.AddSingleton<IRunNetworkInfo, LeaseFileRunNetworkInfo>();
        services.AddSingleton<IExpectedEgress, WireGuardExpectedEgress>();
        services.AddSingleton<IHostGuardProbe, HostGuardProbe>();
        services.AddSingleton<IWorkerStartGate, GuardedWorkerStartGate>();
        services.AddSingleton<LoopMountMediaInspector>();
        services.AddSingleton<IMediaInspector>(sp => sp.GetRequiredService<LoopMountMediaInspector>());
        services.AddHostedService<MediaMountSweepService>();
    }
}

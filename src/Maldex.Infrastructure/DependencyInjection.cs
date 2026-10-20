using Maldex.Core.Abstractions;
using Maldex.Infrastructure.Fakes;
using Maldex.Infrastructure.Guacamole;
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

        if (!OperatingSystem.IsWindows()) services.AddSingleton<IArtifactStore, FileSystemArtifactStore>();
        services.AddSingleton<IDiskSpaceProbe, DiskSpaceProbe>();
        // YARA is real in every mode; the yara binary is a host dependency, not a fake.
        services.AddSingleton<IYaraScanner, YaraCliScanner>();

        switch (mode)
        {
            case "Fake":
                services.AddSingleton<IHypervisor, FakeHypervisor>();
                services.AddSingleton<IAgentChannelFactory, FakeAgentChannelFactory>();
                services.AddSingleton<IEgressRouter, NoopEgressRouter>();
                services.AddSingleton<ISuricataRunner, NoopSuricataRunner>();
                services.AddSingleton<IVmConsoleLocator, FakeConsoleLocator>();
                break;
            case "Real":
                throw new NotSupportedException("Real mode is implemented in Plan 1b");
            default:
                throw new InvalidOperationException($"Unknown Maldex:Mode '{mode}'. Expected 'Fake' or 'Real'.");
        }

        return services;
    }
}

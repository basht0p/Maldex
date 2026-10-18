using Maldex.Core.Abstractions;
using Maldex.Infrastructure.Fakes;
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

        if (!OperatingSystem.IsWindows()) services.AddSingleton<IArtifactStore, FileSystemArtifactStore>();
        services.AddSingleton<IDiskSpaceProbe, DiskSpaceProbe>();

        switch (mode)
        {
            case "Fake":
                services.AddSingleton<IHypervisor, FakeHypervisor>();
                services.AddSingleton<IAgentChannelFactory, FakeAgentChannelFactory>();
                services.AddSingleton<IEgressRouter, NoopEgressRouter>();
                break;
            case "Real":
                throw new NotSupportedException("Real mode is implemented in Plan 1b");
            default:
                throw new InvalidOperationException($"Unknown Maldex:Mode '{mode}'. Expected 'Fake' or 'Real'.");
        }

        return services;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maldex.Orchestrator;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the run coordinator and its options. Requires the execution abstractions (<c>AddMaldexInfrastructure</c>),
    /// <c>MaldexDbContext</c> and an <c>ITaskEventPublisher</c>.
    /// </summary>
    public static IServiceCollection AddMaldexOrchestrator(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.Configure<OrchestratorOptions>(configuration.GetSection(OrchestratorOptions.SectionName));
        services.AddSingleton<RunCoordinator>();
        return services;
    }
}

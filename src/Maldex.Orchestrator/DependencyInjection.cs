using Maldex.Data.Templates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maldex.Orchestrator;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the run coordinator, reconciler, worker (not hosted here) and options. Requires the execution abstractions (<c>AddMaldexInfrastructure</c>),
    /// <c>MaldexDbContext</c> and an <c>ITaskEventPublisher</c>.
    /// </summary>
    public static IServiceCollection AddMaldexOrchestrator(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddMaldexTemplateCatalog(configuration);
        services.Configure<OrchestratorOptions>(configuration.GetSection(OrchestratorOptions.SectionName));
        services.AddSingleton<RunCoordinator>();
        services.AddSingleton<Reconciler>();
        // Hosted by Maldex.Web only when Maldex:Workers:Enabled (Task 13): AddHostedService(sp => sp.GetRequiredService<OrchestratorWorker>()).
        services.AddSingleton<OrchestratorWorker>();
        return services;
    }
}

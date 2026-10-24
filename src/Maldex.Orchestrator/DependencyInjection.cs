using Maldex.Data.Templates;
using Maldex.Orchestrator.Templates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maldex.Orchestrator;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the run coordinator, the template job coordinator, sealing/verification services, reconciler, workers (not hosted here) and options. Requires the execution abstractions (<c>AddMaldexInfrastructure</c>),
    /// <c>MaldexDbContext</c> and an <c>ITaskEventPublisher</c>.
    /// </summary>
    public static IServiceCollection AddMaldexOrchestrator(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddMaldexTemplateCatalog(configuration);
        services.Configure<OrchestratorOptions>(configuration.GetSection(OrchestratorOptions.SectionName));
        services.Configure<TemplateJobOptions>(configuration.GetSection(TemplateJobOptions.SectionName));
        services.AddSingleton<TemplateSealer>();
        services.AddSingleton<TemplateVerifier>();
        services.AddSingleton<TemplateCoordinator>();
        // Hosted by Maldex.Web next to the OrchestratorWorker, behind the same gate.
        services.AddSingleton<TemplateJobWorker>();
        services.AddSingleton<RunCoordinator>();
        services.AddSingleton<Reconciler>();
        // Hosted by Maldex.Web only when Maldex:Workers:Enabled (Task 13): AddHostedService(sp => sp.GetRequiredService<OrchestratorWorker>()).
        services.AddSingleton<OrchestratorWorker>();
        return services;
    }
}

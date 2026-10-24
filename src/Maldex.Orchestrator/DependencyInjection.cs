using Maldex.Data.Templates;
using Maldex.Infrastructure.Real;
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
        // The web host starts in src/Maldex.Web, so the relative component paths must not depend on the working directory: same base as real mode (both modes load the bundle).
        services.PostConfigure<TemplateJobOptions>(o =>
        {
            var configuredRoot = configuration[$"{RealOptions.SectionName}:{nameof(RealOptions.Root)}"];
            var root = string.IsNullOrWhiteSpace(configuredRoot) ? RealPaths.FindRoot(AppContext.BaseDirectory) : Path.GetFullPath(configuredRoot);
            o.AgentExePath = RealPaths.Resolve(root, o.AgentExePath);
            o.AgentVersionPath = RealPaths.Resolve(root, o.AgentVersionPath);
            o.SysmonConfigPath = RealPaths.Resolve(root, o.SysmonConfigPath);
            o.MitmCaPemPath = RealPaths.Resolve(root, o.MitmCaPemPath);
        });
        services.AddSingleton<VmSlotGate>();
        services.AddSingleton<JobConsoleRegistry>();
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

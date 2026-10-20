using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maldex.Processing;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the processing pipeline and worker (not hosted here). Requires <c>AddMaldexInfrastructure</c>
    /// (artifact store, YARA, Suricata), <c>MaldexDbContext</c> and an <c>ITaskEventPublisher</c>.
    /// </summary>
    public static IServiceCollection AddMaldexProcessing(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.Configure<ProcessingOptions>(configuration.GetSection(ProcessingOptions.SectionName));
        services.AddSingleton<ProcessingPipeline>();
        services.AddSingleton<IProcessingPipeline>(sp => sp.GetRequiredService<ProcessingPipeline>());
        // Hosted by Maldex.Web only when Maldex:Workers:Enabled: AddHostedService(sp => sp.GetRequiredService<ProcessingWorker>()).
        services.AddSingleton<ProcessingWorker>();
        services.AddSingleton<RetentionService>();
        // Hosted alongside the others: AddHostedService(sp => sp.GetRequiredService<RetentionWorker>()).
        services.AddSingleton<RetentionWorker>();
        return services;
    }
}

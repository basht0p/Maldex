using Maldex.Core.Agent;
using Maldex.Data.Templates;
using Maldex.Infrastructure.Fakes;
using Maldex.Orchestrator.Media;
using Maldex.Infrastructure.Real;
using Maldex.Orchestrator.Templates;
using Maldex.Orchestrator.Templates.Build;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
        services.Configure<BuildOptions>(configuration.GetSection(BuildOptions.SectionName));
        services.PostConfigure<BuildOptions>(o =>
        {
            var configuredRoot = configuration[$"{RealOptions.SectionName}:{nameof(RealOptions.Root)}"];
            var root = string.IsNullOrWhiteSpace(configuredRoot) ? RealPaths.FindRoot(AppContext.BaseDirectory) : Path.GetFullPath(configuredRoot);
            o.VirtioIsoPath = RealPaths.Resolve(root, o.VirtioIsoPath);
            o.SysmonZipPath = RealPaths.Resolve(root, o.SysmonZipPath);
            o.AutounattendPath = RealPaths.Resolve(root, o.AutounattendPath);
            o.ProvisionScriptPath = RealPaths.Resolve(root, o.ProvisionScriptPath);
            o.OfficeConfigPath = RealPaths.Resolve(root, o.OfficeConfigPath);
            o.WorkDir = RealPaths.Resolve(root, o.WorkDir);
            o.JobLogDir = RealPaths.Resolve(root, o.JobLogDir);
        });
        var mode = configuration["Maldex:Mode"];
        var fakeMode = string.IsNullOrEmpty(mode) || mode == "Fake";
        // A settle window that cannot work would refuse every seal or pass Defender unchecked: fail at startup instead.
        services.AddSingleton<IValidateOptions<TemplateJobOptions>>(new DefenderSettleValidation(fakeMode));
        services.AddOptions<TemplateJobOptions>().ValidateOnStart();
        if (fakeMode)
        {
            services.TryAddSingleton<IProvisioningMediaBuilder, FakeProvisioningMediaBuilder>();
            // The fake guest's Defender state never changes: one reading per check, not a minute of real time (unless configured).
            if (configuration[$"{TemplateJobOptions.SectionName}:{nameof(TemplateJobOptions.DefenderSettleWindow)}"] is null)
                services.Configure<TemplateJobOptions>(o => o.DefenderSettleWindow = TimeSpan.Zero);
            // The fake guest "runs" the agent sealing installed, which is the one ./build.sh agent left on disk: so an Edit, Refresh or
            // Build passes the agent check. Before that agent exists it reports the fake build id (the bundle cannot load then anyway).
            services.Replace(ServiceDescriptor.Singleton(sp =>
            {
                var jobs = sp.GetRequiredService<IOptions<TemplateJobOptions>>();
                return new FakeGuest { InstalledAgentBuildId = () => InstalledAgentBuildId(jobs.Value) };
            }));
        }
        else services.TryAddSingleton<IProvisioningMediaBuilder, XorrisoProvisioningMediaBuilder>();
        services.AddSingleton<VmSlotGate>();
        services.AddSingleton<JobConsoleRegistry>();
        services.AddSingleton<TemplateSealer>();
        services.AddSingleton<TemplateVerifier>();
        services.AddSingleton<TemplateInstaller>();
        services.AddSingleton<TemplateCoordinator>();
        // Hosted by Maldex.Web next to the OrchestratorWorker, behind the same gate.
        services.AddSingleton<TemplateJobWorker>();
        services.AddSingleton<RunCoordinator>();
        services.AddSingleton<Reconciler>();
        // Hosted by Maldex.Web only when Maldex:Workers:Enabled (Task 13): AddHostedService(sp => sp.GetRequiredService<OrchestratorWorker>()).
        services.AddSingleton<OrchestratorWorker>();
        // Media library: intake per request, the upload state and the worker (hosted by Maldex.Web) once.
        services.AddSingleton<MediaUploadState>();
        services.AddSingleton<MediaWorker>();
        services.AddScoped<MediaIntake>();
        return services;
    }

    /// <summary>The build id of the agent on disk (resolved paths), or null when it has not been built.</summary>
    private static string? InstalledAgentBuildId(TemplateJobOptions o)
    {
        try
        {
            return AgentIdentity.BuildIdOfFiles(o.AgentExePath, o.AgentVersionPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private sealed class DefenderSettleValidation(bool fakeMode) : IValidateOptions<TemplateJobOptions>
    {
        public ValidateOptionsResult Validate(string? name, TemplateJobOptions options) =>
            TemplateJobOptions.DefenderSettleProblem(options, fakeMode) is { } problem ? ValidateOptionsResult.Fail(problem) : ValidateOptionsResult.Success;
    }
}

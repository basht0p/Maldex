using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Maldex.Data.Templates;

public static class TemplateCatalogRegistration
{
    /// <summary>Registers the scoped <see cref="TemplateCatalog"/> and binds <see cref="TemplateOptions"/>. Safe to call more than once.</summary>
    public static IServiceCollection AddMaldexTemplateCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        // Bound by hand: Maldex.Data does not reference the configuration binder. Binding twice is harmless.
        var section = configuration.GetSection(TemplateOptions.SectionName);
        services.AddOptions<TemplateOptions>().Configure(o =>
        {
            if (int.TryParse(section[nameof(TemplateOptions.KeepPrevious)], out var keep)) o.KeepPrevious = keep;
        });
        services.TryAddScoped<TemplateCatalog>();
        return services;
    }
}

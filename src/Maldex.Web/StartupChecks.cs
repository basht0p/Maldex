using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace Maldex.Web;

/// <summary>
/// <c>MALDEX_EXTRA_CONFIG</c>: a colon-separated list of JSON files (the packaged <c>/opt/maldex/app/appsettings.Package.json</c>,
/// then the admin's <c>/etc/maldex/appsettings.Production.json</c>), each added in order as optional, after the standard
/// appsettings*.json sources and before the unprefixed environment variables.
/// </summary>
public static class ExtraConfig
{
    public const string EnvironmentVariable = "MALDEX_EXTRA_CONFIG";

    /// <summary>Inserts one optional JSON source per path in <paramref name="paths"/>; returns the index of the first one inserted, or -1.</summary>
    public static int Add(IList<IConfigurationSource> sources, string? paths)
    {
        if (string.IsNullOrWhiteSpace(paths)) return -1;
        var at = sources.ToList().FindLastIndex(x => x is EnvironmentVariablesConfigurationSource { Prefix: null or "" });
        if (at < 0) at = sources.Count;
        var first = -1;
        foreach (var path in paths.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A missing file (or directory) is simply skipped: the source is optional and falls back to an empty provider.
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full)!;
            sources.Insert(at, new JsonConfigurationSource
            {
                Path = Path.GetFileName(full),
                Optional = true,
                ReloadOnChange = false,
                FileProvider = Directory.Exists(dir) ? new PhysicalFileProvider(dir, ExclusionFilters.None) : new NullFileProvider(),
            });
            if (first < 0) first = at;
            at++;
        }
        return first;
    }
}

public static class StartupChecks
{
    public const string MissingConnectionMessage =
        "ConnectionStrings:Maldex is not set; edit /etc/maldex/appsettings.Production.json and restart maldex";
}

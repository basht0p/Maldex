using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace Maldex.Web;

public static class RealSettings
{
    public const string FileName = "appsettings.Real.json";

    /// <summary>
    /// Inserts <c>appsettings.Real.json</c> immediately before the last unprefixed environment-variables source: it overrides
    /// appsettings.json / appsettings.{Environment}.json (and the ASPNETCORE_-prefixed variables) but environment variables
    /// and the command line still override it.
    /// </summary>
    public static void Insert(IList<IConfigurationSource> sources, IFileProvider contentRoot)
    {
        var at = sources.ToList().FindLastIndex(x => x is EnvironmentVariablesConfigurationSource { Prefix: null or "" });
        sources.Insert(at < 0 ? sources.Count : at, new JsonConfigurationSource { Path = FileName, Optional = true, FileProvider = contentRoot });
    }
}

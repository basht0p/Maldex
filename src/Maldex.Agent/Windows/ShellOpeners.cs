using System.Diagnostics;
using System.Runtime.Versioning;
using Maldex.Agent.Commands;

namespace Maldex.Agent.Windows;

[SupportedOSPlatform("windows")]
public sealed class ShellSampleOpener : ISampleOpener
{
    public Task OpenAsync(string filePath, CancellationToken ct)
    {
        using var p = Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

[SupportedOSPlatform("windows")]
public sealed class ShellUrlOpener : IUrlOpener
{
    public Task OpenAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Only absolute http/https URLs can be opened.");
        using var p = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

using System.Diagnostics;
using System.Runtime.Versioning;
using Maldex.Agent.Commands;

namespace Maldex.Agent.Windows;

[SupportedOSPlatform("windows")]
public sealed class ShellSampleOpener : ISampleOpener
{
    public Task OpenAsync(string filePath, CancellationToken ct)
    {
        var launch = SampleLaunch.For(filePath, Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows");
        var psi = new ProcessStartInfo(launch.FileName) { UseShellExecute = launch.UseShellExecute };
        foreach (var a in launch.Arguments) psi.ArgumentList.Add(a);
        if (!launch.UseShellExecute) psi.WorkingDirectory = Path.GetDirectoryName(filePath) ?? "";
        using var p = Process.Start(psi);
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

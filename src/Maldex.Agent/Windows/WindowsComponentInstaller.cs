using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Maldex.Agent.Commands;

namespace Maldex.Agent.Windows;

/// <summary>
/// The real <c>apply_components</c> steps. Runs as the logged-on analyst, who is an administrator in the guest
/// (the machine certificate store, <c>C:\Windows</c> and <c>C:\Program Files</c> all need it).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsComponentInstaller : IComponentInstaller
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);

    public void ImportCaCertificate(string derPath)
    {
        using var cert = X509CertificateLoader.LoadCertificateFromFile(derPath);
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        // Replace an older certificate with the same subject (a re-issued CA), but keep one that is already this exact certificate.
        foreach (var existing in store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, cert.Subject, validOnly: false))
        {
            if (existing.Thumbprint != cert.Thumbprint) store.Remove(existing);
            existing.Dispose();
        }
        if (store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false).Count == 0) store.Add(cert);
    }

    public void ApplySysmonConfig(string configPath)
    {
        var sysmon = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysmon64.exe");
        if (!File.Exists(sysmon)) throw new FileNotFoundException("Sysmon is not installed", sysmon);
        Run(sysmon, ["-accepteula", "-c", configPath]);
    }

    public void SwapAgent(string newExePath)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("the agent's own path is unknown");
        AgentExeSwap.Swap(exe, newExePath);
    }

    public void ClearLogsAndTemp()
    {
        // Logs that cannot be cleared (some analytic channels) are skipped: the template is still clean for the ones that matter.
        foreach (var log in Run("wevtutil.exe", ["el"]).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { Run("wevtutil.exe", ["cl", log]); }
            catch (InvalidOperationException) { }
        }
        foreach (var dir in new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") })
            ClearDirectory(dir);
    }

    private static void ClearDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            try
            {
                if (entry is DirectoryInfo d) d.Delete(recursive: true);
                else entry.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // in use: left alone
        }
    }

    /// <summary>Runs a tool to completion and returns its standard output; throws with its output when it fails or hangs.</summary>
    private static string Run(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{Path.GetFileName(file)} did not start");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(ToolTimeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException($"{Path.GetFileName(file)} timed out after {ToolTimeout.TotalSeconds:0} s");
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(file)} exited with {p.ExitCode}: {(stdout.Result + stderr.Result).Trim()}");
        return stdout.Result;
    }
}

/// <summary>
/// Powers the guest off with <c>shutdown.exe /s /t 0 /f</c>. The tool enables the shutdown privilege itself, which a direct
/// <c>InitiateSystemShutdownEx</c> call from the analyst's unelevated-privilege token would have to do by hand.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerController : IPowerController
{
    public void PowerOff()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "/s", "/t", "0", "/f" }) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }
}

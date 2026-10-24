using System.Management;
using System.Runtime.Versioning;
using Maldex.Agent.Commands;
using Microsoft.Win32;

namespace Maldex.Agent.Windows;

/// <summary>Reads the guest's Windows edition/build, Office presence and Defender real-time protection. Read-only.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProvenanceProbe : IProvenanceProbe
{
    public ProvenanceReport Probe()
    {
        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = nt?.GetValue("CurrentBuild") as string;
        if (string.IsNullOrWhiteSpace(build)) throw new InvalidOperationException("CurrentBuild is missing from HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion");
        var edition = EditionFormat.FormatEdition(nt!.GetValue("ProductName") as string, nt.GetValue("EditionID") as string, build);
        var ubr = nt.GetValue("UBR");
        return new ProvenanceReport(edition, ubr is null ? build : $"{build}.{ubr}", OfficePresent(), RealTimeProtection());
    }

    private static bool? OfficePresent()
    {
        try
        {
            using var c2r = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
            if (c2r is not null) return true;
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (string.IsNullOrEmpty(pf) || !Directory.Exists(Path.Combine(pf, "Microsoft Office"))) continue;
                if (Directory.EnumerateFiles(Path.Combine(pf, "Microsoft Office"), "WINWORD.EXE", SearchOption.AllDirectories).Any()) return true;
            }
            return false;
        }
        catch { return null; }
    }

    private static bool? RealTimeProtection()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT RealTimeProtectionEnabled FROM MSFT_MpComputerStatus");
            searcher.Options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = false };
            foreach (ManagementBaseObject o in searcher.Get())
                using (o) return o["RealTimeProtectionEnabled"] as bool?;
            return null;
        }
        catch { return null; }
    }
}

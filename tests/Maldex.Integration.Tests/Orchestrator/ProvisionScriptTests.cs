using System.Diagnostics;
using Maldex.Infrastructure.Real;
using Maldex.Orchestrator.Templates.Build;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

public sealed class ProvisionScriptTests
{
    private static readonly string ScriptPath = Path.Combine(RealPaths.FindRoot(AppContext.BaseDirectory), "deploy/guest/provision/Provision.ps1");

    private static bool PwshAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pwsh", "-NoProfile -Command exit") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    [Fact]
    public void ProvisionScript_Parses()
    {
        Assert.SkipUnless(PwshAvailable(), "pwsh is not installed (./build.sh real-setup installs it); skipping the Provision.ps1 parse check.");
        var psi = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["MALDEX_SCRIPT"] = ScriptPath;
        foreach (var a in new[] { "-NoProfile", "-Command",
            "$e = $null; [System.Management.Automation.Language.Parser]::ParseFile($env:MALDEX_SCRIPT, [ref]$null, [ref]$e) | Out-Null; if ($e) { $e | ForEach-Object { $_.ToString() }; exit 1 }" })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0, "Provision.ps1 has parse errors:\n" + output);
    }

    [Fact]
    public void ProvisionScript_WritesTheResultMarker_AndNeverShutsDown()
    {
        var script = File.ReadAllText(ScriptPath);
        Assert.Contains(BuildOptions.ProvisionResultGuestPath, script);
        Assert.Contains(".tmp", script); // written to a temp name and renamed
        Assert.DoesNotContain("shutdown", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stop-Computer", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wevtutil", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProvisionScript_StartsTheAgentEarly_AndHidesTheEdgeFirstRun()
    {
        var script = File.ReadAllText(ScriptPath);
        var agent = script.IndexOf("Start-ScheduledTask -TaskName 'MaldexAgent'", StringComparison.Ordinal);
        Assert.True(agent > 0, "the agent task is started by the script");
        Assert.True(agent < script.IndexOf("Step 'Sysmon'", StringComparison.Ordinal), "the agent is started before Sysmon");
        Assert.Contains("-Name HideFirstRunExperience -Value 1", script);
    }
}

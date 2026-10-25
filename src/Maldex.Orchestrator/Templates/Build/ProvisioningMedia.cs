using System.IO.Compression;
using Maldex.Infrastructure.Execution;
using Microsoft.Extensions.Options;

namespace Maldex.Orchestrator.Templates.Build;

/// <summary>What goes on the provisioning CD: the bundle's bytes, the rendering parameters for the unattend file, and whether Office is installed.</summary>
public sealed record ProvisioningInputs(ComponentBundle Bundle, int ImageIndex, string? ProductKey, bool IncludeOffice);

public interface IProvisioningMediaBuilder
{
    /// <summary>Builds <c>&lt;workDir&gt;/provision.iso</c> and returns its path.</summary>
    Task<string> BuildAsync(ProvisioningInputs inputs, string workDir, CancellationToken ct);
}

/// <summary>Writes the stage directory (<c>&lt;workDir&gt;/stage</c>) that both builders share.</summary>
internal static class ProvisioningStage
{
    public static async Task<string> WriteAsync(BuildOptions options, ProvisioningInputs inputs, string workDir, bool placeholderSysmon, CancellationToken ct)
    {
        var stage = Path.Combine(workDir, "stage");
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        Directory.CreateDirectory(stage);

        var template = await File.ReadAllTextAsync(options.AutounattendPath, ct).ConfigureAwait(false);
        var rendered = AutounattendRenderer.Render(template, inputs.ImageIndex, inputs.ProductKey);
        await File.WriteAllTextAsync(Path.Combine(stage, "Autounattend.xml"), rendered, new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);

        File.Copy(options.ProvisionScriptPath, Path.Combine(stage, "Provision.ps1"), overwrite: true);
        await File.WriteAllBytesAsync(Path.Combine(stage, "sysmon-config.xml"), inputs.Bundle.SysmonConfig, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(stage, "Maldex.Agent.exe"), inputs.Bundle.AgentExe, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(stage, "maldex-ca.cer"), inputs.Bundle.CaDer, ct).ConfigureAwait(false);
        await WriteSysmonAsync(options.SysmonZipPath, Path.Combine(stage, "Sysmon64.exe"), placeholderSysmon, ct).ConfigureAwait(false);
        if (inputs.IncludeOffice) File.Copy(options.OfficeConfigPath, Path.Combine(stage, "office-config.xml"), overwrite: true);
        return stage;
    }

    private static async Task WriteSysmonAsync(string zipPath, string dest, bool placeholderIfMissing, CancellationToken ct)
    {
        if (!File.Exists(zipPath))
        {
            if (placeholderIfMissing) { await File.WriteAllBytesAsync(dest, [], ct).ConfigureAwait(false); return; }
            throw new InvalidOperationException($"Sysmon.zip is missing: {zipPath} does not exist; run ./build.sh real-setup");
        }
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("Sysmon64.exe")
            ?? throw new InvalidOperationException($"{zipPath} has no Sysmon64.exe; delete it and run ./build.sh real-setup");
        await using var src = entry.Open();
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }
}

/// <summary>Builds the ISO with <c>xorriso -as mkisofs</c> (label MALDEXPROV); the stage is deleted afterwards.</summary>
public sealed class XorrisoProvisioningMediaBuilder(IProcessRunner runner, IOptions<BuildOptions> options) : IProvisioningMediaBuilder
{
    public async Task<string> BuildAsync(ProvisioningInputs inputs, string workDir, CancellationToken ct)
    {
        var opts = options.Value;
        Directory.CreateDirectory(workDir);
        var iso = Path.Combine(workDir, "provision.iso");
        var stage = Path.Combine(workDir, "stage");
        var built = false;
        try
        {
            // Inside the try: a missing Sysmon.zip or a cancel while staging must not leave the agent and the rendered unattend behind.
            stage = await ProvisioningStage.WriteAsync(opts, inputs, workDir, placeholderSysmon: false, ct).ConfigureAwait(false);
            if (File.Exists(iso)) File.Delete(iso);
            var result = await runner.RunAsync(opts.XorrisoBinary,
                ["-as", "mkisofs", "-quiet", "-J", "-R", "-V", "MALDEXPROV", "-o", iso, stage], ct,
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"xorriso failed with exit code {result.ExitCode}: {result.Stderr.Trim()}");
            built = true;
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (!built) try { File.Delete(iso); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return iso;
    }
}

/// <summary>Fake mode: writes the stage (kept, for inspection) and an empty ISO file.</summary>
public sealed class FakeProvisioningMediaBuilder(IOptions<BuildOptions> options) : IProvisioningMediaBuilder
{
    public async Task<string> BuildAsync(ProvisioningInputs inputs, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        await ProvisioningStage.WriteAsync(options.Value, inputs, workDir, placeholderSysmon: true, ct).ConfigureAwait(false);
        var iso = Path.Combine(workDir, "provision.iso");
        await File.WriteAllBytesAsync(iso, [], ct).ConfigureAwait(false);
        return iso;
    }
}

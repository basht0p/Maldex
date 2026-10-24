using System.ComponentModel;
using System.Globalization;
using Maldex.Core.Abstractions;
using Maldex.Core.Text;
using Maldex.Infrastructure.Execution;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Libvirt;

/// <summary>
/// Writes the guest agent into a working copy's Windows volume with libguestfs (<c>guestfish</c>), while no VM runs on it.
/// guestfish runs as the app's own user with the fixed appliance from <c>./build.sh real-setup</c>; no shell is involved
/// (argv array, and the environment travels through <c>env</c>).
/// </summary>
/// <remarks>
/// The caller bounds the run with its token (the coordinator applies <c>AgentInstallTimeout</c>); <see cref="HardCap"/> only
/// stops a hung guestfish from outliving every caller.
/// </remarks>
public sealed class GuestfishDiskEditor(IProcessRunner runner, IOptions<LibvirtOptions> options) : IGuestDiskEditor
{
    /// <summary>Where the agent lives in the image (Provision.ps1 <c>$AgentDir</c>); libguestfs addresses the C: volume as the root.</summary>
    public const string GuestAgentPath = "/Program Files/Maldex/Maldex.Agent.exe";

    private static readonly TimeSpan HardCap = TimeSpan.FromMinutes(15);
    private const UnixFileMode OwnerWrite = UnixFileMode.UserWrite;
    private const string Failed = "writing the agent into the working copy failed";

    private readonly LibvirtOptions _o = options.Value;

    public async Task InstallAgentAsync(string fileStem, string agentExePath, CancellationToken ct)
    {
        var disk = LibvirtTemplateStore.DiskPath(_o, fileStem); // rejects anything but a lowercase slug
        if (!File.Exists(disk)) throw new TemplateAgentInstallException($"{Failed}: the working copy's disk is missing");
        // A sealed version disk (0444) is never edited. The mode is what the store sets and checks the same way.
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(disk) & OwnerWrite) == 0)
            throw new TemplateAgentInstallException($"{Failed}: the disk is a sealed version, not a working copy");

        long expected;
        try
        {
            expected = new FileInfo(agentExePath).Length;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new TemplateAgentInstallException($"{Failed}: the guest agent is missing: {agentExePath} does not exist; run ./build.sh agent", ex);
        }

        ProcessResult result;
        try
        {
            result = await runner.RunAsync("env",
            [
                "LIBGUESTFS_BACKEND=direct", "LIBGUESTFS_PATH=" + _o.GuestfsAppliancePath,
                _o.GuestfishBinary, "--format=qcow2", "-a", disk, "-i", "--",
                "upload", agentExePath, GuestAgentPath, ":", "filesize", GuestAgentPath,
            ], ct, timeout: HardCap).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TemplateAgentInstallException($"{Failed}: guestfish did not finish");
        }
        catch (Win32Exception ex)
        {
            throw new TemplateAgentInstallException($"{Failed}: guestfish could not be started ({OneLine(ex.Message)}); run ./build.sh real-setup", ex);
        }

        if (result.ExitCode != 0)
            throw new TemplateAgentInstallException($"{Failed} (guestfish exit {result.ExitCode}): {OneLine(result.Stderr)}");

        var answer = result.Stdout.Trim().Split('\n')[^1].Trim();
        if (!long.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var written))
            throw new TemplateAgentInstallException($"{Failed}: guestfish did not report the written size ({OneLine(answer)})");
        if (written != expected)
            throw new TemplateAgentInstallException($"{Failed}: {written} bytes were written, the agent is {expected} bytes");
    }

    /// <summary>Process output as one short line, safe to store as a failure reason.</summary>
    private static string OneLine(string text)
    {
        var line = string.Join(' ', GuestText.Clean(text, 2000).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= 250 ? line : line[..249] + "…";
    }
}

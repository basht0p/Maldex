using System.Security.Cryptography;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Media;
using Maldex.Infrastructure.Execution;
using Maldex.Infrastructure.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Media;

/// <summary>
/// Looks inside an ISO with a read-only loop mount (<c>ro,nosuid,nodev,noexec</c>) and <c>wiminfo</c>. Nothing from the mount is ever
/// executed; the file system is only listed and one file handed to wiminfo. Symbolic links on the (untrusted) image are ignored.
/// The mount is always removed in a <c>finally</c>.
/// </summary>
public sealed class LoopMountMediaInspector(IProcessRunner runner, IOptions<MediaOptions> options, ILogger<LoopMountMediaInspector> log) : IMediaInspector
{
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(30);

    public async Task<MediaInspection> InspectAsync(string isoPath, MediaKind kind, CancellationToken ct)
    {
        var o = options.Value;
        var mountRoot = Path.Combine(o.Root, "mnt");
        Directory.CreateDirectory(mountRoot);
        var mnt = Path.Combine(mountRoot, RandomNumberGenerator.GetHexString(16, lowercase: true));
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("loop mounts need Linux");
        Directory.CreateDirectory(mnt, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var mountAttempted = false;
        try
        {
            var (mountFile, mountArgs) = PrivilegedCommands.Sudo("mount", "-o", "loop,ro,nosuid,nodev,noexec", "-t", "udf,iso9660", isoPath, mnt);
            ProcessResult mount;
            mountAttempted = true;
            try { mount = await runner.RunAsync(mountFile, mountArgs, ct, timeout: MountTimeout); }
            catch (TimeoutException) { mount = new ProcessResult(-1, "", "mount timed out"); }
            if (mount.ExitCode != 0)
            {
                log.LogWarning("Mounting {Iso} failed ({Code}): {Stderr}", isoPath, mount.ExitCode, mount.Stderr.Trim());
                return new MediaInspection(null, "the file could not be mounted read-only as an ISO (udf/iso9660)");
            }

            return kind == MediaKind.Application
                ? new MediaInspection(new MediaContents([], DetectOffice(mnt) ? "Office" : null), null)
                : await InspectInstallerAsync(mnt, o, ct);
        }
        finally
        {
            await UnmountAsync(mnt, mountAttempted);
        }
    }

    private async Task<MediaInspection> InspectInstallerAsync(string mnt, MediaOptions o, CancellationToken ct)
    {
        const string NotInstaller = "not a Windows installer: no sources/install.wim or install.esd";
        var sources = FindEntry(mnt, "sources", directory: true);
        if (sources is null) return new MediaInspection(null, NotInstaller);
        var image = FindEntry(sources, "install.wim", false) ?? FindEntry(sources, "install.esd", false);
        if (image is null)
            return FindEntry(sources, "install.swm", false) is not null
                ? new MediaInspection(null, "split install images (install.swm) are not supported")
                : new MediaInspection(null, NotInstaller);

        ProcessResult info;
        try { info = await runner.RunAsync(o.WiminfoBinary, [image], ct, timeout: o.InspectTimeout); }
        catch (TimeoutException) { return new MediaInspection(null, "reading the install image timed out"); }
        if (info.ExitCode != 0)
        {
            log.LogWarning("wiminfo failed ({Code}): {Stderr}", info.ExitCode, info.Stderr.Trim());
            return new MediaInspection(null, "the install image could not be read");
        }
        var images = WimInfoParser.Parse(info.Stdout);
        return images.Count == 0 ? new MediaInspection(null, NotInstaller) : new MediaInspection(new MediaContents(images, null), null);
    }

    private static bool DetectOffice(string mnt)
    {
        if (FindEntry(mnt, "setup.exe", directory: false) is null) return false;
        var office = FindEntry(mnt, "Office", directory: true);
        return office is not null && FindEntry(office, "Data", directory: true) is not null;
    }

    /// <summary>A direct child of <paramref name="dir"/> named <paramref name="name"/> (ignoring case), never a symbolic link.</summary>
    private static string? FindEntry(string dir, string name, bool directory)
    {
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (!string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.LinkTarget is not null) continue;
            if ((entry is DirectoryInfo) != directory) continue;
            return entry.FullName;
        }
        return null;
    }

    private async Task UnmountAsync(string mnt, bool mountAttempted)
    {
        var unmounted = !mountAttempted;
        if (mountAttempted)
        {
            // Never cancelled: leaving a loop mount behind is worse than a slow request.
            try
            {
                var (file, args) = PrivilegedCommands.Sudo("umount", mnt);
                unmounted = (await runner.RunAsync(file, args, CancellationToken.None, timeout: MountTimeout)).ExitCode == 0;
            }
            catch (Exception e) { log.LogWarning(e, "umount {Mount} threw", mnt); }
            if (!unmounted)
            {
                try
                {
                    var (file, args) = PrivilegedCommands.Sudo("umount", "-l", mnt);
                    unmounted = (await runner.RunAsync(file, args, CancellationToken.None, timeout: MountTimeout)).ExitCode == 0;
                    log.LogWarning("umount {Mount} failed; lazy unmount {Result}", mnt, unmounted ? "succeeded" : "failed too");
                }
                catch (Exception e) { log.LogError(e, "lazy umount {Mount} threw", mnt); }
            }
        }
        if (!unmounted) return; // never delete through a live mount
        try { Directory.Delete(mnt); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning(e, "could not remove mount point {Mount}", mnt); }
    }
}

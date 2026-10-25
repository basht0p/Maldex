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
public sealed class LoopMountMediaInspector(
    IProcessRunner runner, IOptions<MediaOptions> options, ILogger<LoopMountMediaInspector> log, Func<string>? readMountInfo = null) : IMediaInspector
{
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(30);
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly Func<string> _readMountInfo = readMountInfo ?? (() => File.ReadAllText("/proc/self/mountinfo"));

    public async Task<MediaInspection> InspectAsync(string isoPath, MediaKind kind, CancellationToken ct)
    {
        var o = options.Value;
        var root = RequireRoot(o);
        // These two paths go into a root-run argv: they must be absolute (never parsed as an option) and the image must live in the media root.
        if (string.IsNullOrEmpty(isoPath) || !Path.IsPathRooted(isoPath) || isoPath.Split('/').Contains(".."))
            throw new ArgumentException("the ISO path must be absolute and free of '..'", nameof(isoPath));
        var fullIso = Path.GetFullPath(isoPath);
        if (!fullIso.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("the ISO must be inside the media root", nameof(isoPath));
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("loop mounts need Linux");

        var mountRoot = EnsureMountRoot(root);
        var mnt = Path.Combine(mountRoot, RandomNumberGenerator.GetHexString(16, lowercase: true));
        Directory.CreateDirectory(mnt, OwnerOnly);

        var mounted = false;
        try
        {
            var (mountFile, mountArgs) = PrivilegedCommands.Sudo("mount", "-o", "loop,ro,nosuid,nodev,noexec", "-t", "udf,iso9660", "--", fullIso, mnt);
            ProcessResult mount;
            try { mount = await runner.RunAsync(mountFile, mountArgs, ct, timeout: MountTimeout); }
            catch (TimeoutException) { mount = new ProcessResult(-1, "", "mount timed out"); }
            if (mount.ExitCode != 0)
            {
                log.LogWarning("Mounting {Iso} failed ({Code}): {Stderr}", isoPath, mount.ExitCode, mount.Stderr.Trim());
                return new MediaInspection(null, "the file could not be mounted read-only as an ISO (udf/iso9660)");
            }
            mounted = true;

            return kind == MediaKind.Application
                ? new MediaInspection(new MediaContents([], DetectOffice(mnt) ? "Office" : null), null)
                : await InspectInstallerAsync(mnt, o, ct);
        }
        finally
        {
            if (mounted) await UnmountAsync(mnt);
            // rmdir (not recursive): it fails with EBUSY on a live mount and deletes nothing, so it is always safe to try.
            RemoveMountPoint(mnt);
        }
    }

    /// <summary>
    /// Unmounts and removes every leftover <c>{Root}/mnt/*</c>. A mount that completed after its caller timed out (ProcessRunner kills sudo
    /// but not the root-owned mount child) can only be found this way. Run once at startup and by the media worker.
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var root = RequireRoot(options.Value);
        var mountRoot = Path.Combine(root, "mnt");
        if (!Directory.Exists(mountRoot)) return;
        var active = MountPoints(_readMountInfo());
        foreach (var dir in Directory.EnumerateDirectories(mountRoot))
        {
            ct.ThrowIfCancellationRequested();
            if (active.Contains(dir))
            {
                try
                {
                    var (file, args) = PrivilegedCommands.Sudo("umount", "-l", "--", dir);
                    var r = await runner.RunAsync(file, args, ct, timeout: MountTimeout);
                    if (r.ExitCode != 0) log.LogWarning("Sweep: lazy umount of {Mount} failed: {Stderr}", dir, r.Stderr.Trim());
                }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Sweep: umount {Mount} threw", dir); }
            }
            RemoveMountPoint(dir);
        }
    }

    private static HashSet<string> MountPoints(string mountInfo)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in mountInfo.Split('\n'))
        {
            var fields = line.Split(' ');
            if (fields.Length > 4) set.Add(fields[4].Replace("\\040", " ").Replace("\\011", "\t").Replace("\\134", "\\"));
        }
        return set;
    }

    private static string RequireRoot(MediaOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.Root) || !Path.IsPathRooted(o.Root))
            throw new InvalidOperationException("Maldex:Media:Root must be an absolute path");
        return Path.GetFullPath(o.Root).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string EnsureMountRoot(string root)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("loop mounts need Linux");
        Directory.CreateDirectory(root); // the root keeps the default mode: libvirt reads the ISOs inside it
        var mountRoot = Path.Combine(root, "mnt");
        Directory.CreateDirectory(mountRoot, OwnerOnly);
        return mountRoot;
    }

    private void RemoveMountPoint(string mnt)
    {
        try { Directory.Delete(mnt, recursive: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning(e, "could not remove mount point {Mount}", mnt); }
    }

    private async Task<MediaInspection> InspectInstallerAsync(string mnt, MediaOptions o, CancellationToken ct)
    {
        const string NotInstaller = "not a Windows installer: no sources/install.wim or install.esd";
        var sources = FindEntry(mnt, "sources", directory: true);
        if (sources is null) return new MediaInspection(null, NotInstaller);
        var image = await RegularFileAsync(FindEntry(sources, "install.wim", false), ct) ?? await RegularFileAsync(FindEntry(sources, "install.esd", false), ct);
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

    /// <summary>The path if it is a regular file (lstat): never a FIFO, device or directory, which wiminfo would block on or misread.</summary>
    private async Task<string?> RegularFileAsync(string? path, CancellationToken ct)
    {
        if (path is null) return null;
        var r = await runner.RunAsync("stat", ["-c", "%F", "--", path], ct, timeout: MountTimeout);
        return r.ExitCode == 0 && r.Stdout.Trim().StartsWith("regular", StringComparison.Ordinal) ? path : null;
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

    private async Task UnmountAsync(string mnt)
    {
        // Never cancelled: leaving a loop mount behind is worse than a slow request.
        var unmounted = false;
        try
        {
            var (file, args) = PrivilegedCommands.Sudo("umount", "--", mnt);
            unmounted = (await runner.RunAsync(file, args, CancellationToken.None, timeout: MountTimeout)).ExitCode == 0;
        }
        catch (Exception e) { log.LogWarning(e, "umount {Mount} threw", mnt); }
        if (unmounted) return;
        try
        {
            var (file, args) = PrivilegedCommands.Sudo("umount", "-l", "--", mnt);
            unmounted = (await runner.RunAsync(file, args, CancellationToken.None, timeout: MountTimeout)).ExitCode == 0;
            log.LogWarning("umount {Mount} failed; lazy unmount {Result}", mnt, unmounted ? "succeeded" : "failed too");
        }
        catch (Exception e) { log.LogError(e, "lazy umount {Mount} threw", mnt); }
    }
}

using System.Text.RegularExpressions;
using Maldex.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Libvirt;

/// <summary>Template version disks in <c>BaseImageDir</c>: <c>{stem}.qcow2</c> and <c>{stem}.vars.fd</c>.</summary>
public sealed partial class LibvirtTemplateStore(IOptions<LibvirtOptions> options) : ITemplateStore
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}\z")] // \z, not $: $ also matches before a trailing newline
    private static partial Regex StemPattern();

    private readonly LibvirtOptions _o = options.Value;

    public bool Exists(string fileStem)
    {
        var (disk, vars) = Paths(fileStem);
        return File.Exists(disk) && File.Exists(vars);
    }

    public long? SizeOf(string fileStem)
    {
        var (disk, _) = Paths(fileStem);
        return File.Exists(disk) ? new FileInfo(disk).Length : null;
    }

    public Task DeleteAsync(string fileStem, CancellationToken ct)
    {
        var (disk, vars) = Paths(fileStem);
        // Finalized images are 0444; unlinking needs write access to the directory only, which the process owns. The .tmp
        // files are what a crash in the middle of CreateWorkingCopyAsync leaves behind.
        File.Delete(disk);
        File.Delete(vars);
        File.Delete(disk + ".tmp");
        File.Delete(vars + ".tmp");
        return Task.CompletedTask;
    }

    public async Task CreateWorkingCopyAsync(string fromStem, string toStem, CancellationToken ct)
    {
        var (srcDisk, srcVars) = Paths(fromStem);
        var (dstDisk, dstVars) = Paths(toStem);
        if (File.Exists(dstDisk) || File.Exists(dstVars))
            throw new InvalidOperationException($"Template file stem '{toStem}' already exists.");
        if (!File.Exists(srcDisk) || !File.Exists(srcVars))
            throw new FileNotFoundException($"Template file stem '{fromStem}' not found.");
        var tmpDisk = dstDisk + ".tmp";
        var tmpVars = dstVars + ".tmp";
        try
        {
            await CopyWritableAsync(srcDisk, tmpDisk, ct);
            await CopyWritableAsync(srcVars, tmpVars, ct);
            File.Move(tmpDisk, dstDisk);
            File.Move(tmpVars, dstVars);
        }
        catch
        {
            foreach (var f in new[] { tmpDisk, tmpVars, dstDisk, dstVars }) try { File.Delete(f); } catch { /* best effort */ }
            throw;
        }
    }

    public Task SealAsync(string fileStem, CancellationToken ct)
    {
        var (disk, vars) = Paths(fileStem);
        const UnixFileMode readOnly = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(disk, readOnly);
            File.SetUnixFileMode(vars, readOnly);
        }
        return Task.CompletedTask;
    }

    // Not File.Copy: it keeps the source mode (0444 for a version disk). Create at 0600 from the start.
    private static async Task CopyWritableAsync(string src, string dst, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var from = File.OpenRead(src);
        await using var to = new FileStream(dst, options);
        await from.CopyToAsync(to, ct);
    }

    /// <summary>Rejects anything but a lowercase slug so a stem can never escape the base image directory.</summary>
    private (string Disk, string Vars) Paths(string fileStem)
    {
        var root = LibvirtPaths.BaseRoot(_o);
        return (DiskPath(_o, fileStem), Path.Combine(root, fileStem + ".vars.fd"));
    }

    /// <summary>The stem's disk file in the base image directory; the stem is validated like every other use of it.</summary>
    internal static string DiskPath(LibvirtOptions o, string fileStem)
    {
        if (fileStem is null || !StemPattern().IsMatch(fileStem))
            throw new ArgumentException($"Invalid template file stem '{fileStem}'.", nameof(fileStem));
        return Path.Combine(LibvirtPaths.BaseRoot(o), fileStem + ".qcow2");
    }
}

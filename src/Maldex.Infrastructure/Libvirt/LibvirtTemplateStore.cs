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
        // Finalized images are 0444; unlinking needs write access to the directory only, which the process owns.
        File.Delete(disk);
        File.Delete(vars);
        return Task.CompletedTask;
    }

    /// <summary>Rejects anything but a lowercase slug so a stem can never escape the base image directory.</summary>
    private (string Disk, string Vars) Paths(string fileStem)
    {
        if (fileStem is null || !StemPattern().IsMatch(fileStem))
            throw new ArgumentException($"Invalid template file stem '{fileStem}'.", nameof(fileStem));
        var root = LibvirtPaths.BaseRoot(_o);
        return (Path.Combine(root, fileStem + ".qcow2"), Path.Combine(root, fileStem + ".vars.fd"));
    }
}

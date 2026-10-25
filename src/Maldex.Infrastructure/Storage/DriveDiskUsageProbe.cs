using Maldex.Core.Abstractions;

namespace Maldex.Infrastructure.Storage;

/// <summary>Measures the volume under a path without creating it: a missing directory is measured through its nearest existing ancestor.</summary>
public sealed class DriveDiskUsageProbe : IDiskUsageProbe
{
    public DiskUsage Usage(string path)
    {
        var dir = Path.GetFullPath(path);
        while (!Directory.Exists(dir) && Path.GetDirectoryName(dir) is { } parent) dir = parent;
        var drive = new DriveInfo(dir);
        return new DiskUsage(dir, drive.AvailableFreeSpace, drive.TotalSize);
    }
}

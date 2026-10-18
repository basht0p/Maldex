using Maldex.Core.Abstractions;

namespace Maldex.Infrastructure.Storage;

public sealed class DiskSpaceProbe : IDiskSpaceProbe
{
    public long FreeMiB(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(full);
        return new DriveInfo(full).AvailableFreeSpace / (1024 * 1024);
    }
}

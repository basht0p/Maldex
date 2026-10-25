namespace Maldex.Core.Abstractions;

/// <summary>Path = the directory actually measured (the nearest existing ancestor when the asked path is missing).</summary>
public sealed record DiskUsage(string Path, long FreeBytes, long TotalBytes);

public interface IDiskUsageProbe
{
    DiskUsage Usage(string path);
}

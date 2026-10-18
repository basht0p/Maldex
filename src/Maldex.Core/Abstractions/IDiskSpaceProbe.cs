namespace Maldex.Core.Abstractions;

public interface IDiskSpaceProbe
{
    long FreeMiB(string path);
}

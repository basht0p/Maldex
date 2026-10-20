namespace Maldex.Infrastructure.Real;

/// <summary>Bound from <c>Maldex:Real</c>: the one base directory and the one RunDir every real-mode path agrees on.</summary>
public sealed class RealOptions
{
    public const string SectionName = "Maldex:Real";

    /// <summary>Base for every relative real-mode path. Empty means "the directory that contains <c>Maldex.slnx</c>".</summary>
    public string Root { get; set; } = "";

    /// <summary>Per-run files live under <c>{RunDir}/{vmName}/</c>. Overrides the RunDir of the Libvirt, Agent and Net sections.</summary>
    public string RunDir { get; set; } = ".data/vm/run";
}

public static class RealPaths
{
    public const string RootMarker = "Maldex.slnx";

    /// <summary>Walks up from <paramref name="startDir"/> to the directory that contains <c>Maldex.slnx</c>.</summary>
    public static string FindRoot(string startDir)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(startDir)); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, RootMarker))) return dir.FullName;
        throw new InvalidOperationException(
            $"Real mode needs a base directory for its relative paths, but no {RootMarker} was found above '{startDir}'. " +
            "Set Maldex:Real:Root to the repository (or install) directory.");
    }

    /// <summary>Absolute <paramref name="path"/> unchanged; a relative one is resolved against <paramref name="root"/>. Only for file-system options, never for binary names looked up on PATH.</summary>
    public static string Resolve(string root, string path) =>
        string.IsNullOrEmpty(path) || Path.IsPathRooted(path) ? path : Path.GetFullPath(path, root);
}

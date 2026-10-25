namespace Maldex.Core.Media;

public sealed record EditionChoice(int Index, string Name, string? ProductKey);

public sealed class EditionNotSupportedException(string message) : Exception(message);

/// <summary>Chooses the Windows edition to install. Keys are the public generic KMS client setup keys; evaluation media takes none.</summary>
public static class EditionSelector
{
    private const string EnterpriseEvaluation = "Windows 11 Enterprise Evaluation";
    private const string Enterprise = "Windows 11 Enterprise";
    private const string Pro = "Windows 11 Pro";
    private const string Education = "Windows 11 Education";
    private const string Home = "Windows 11 Home";

    private static readonly Dictionary<string, string?> Supported = new(StringComparer.Ordinal)
    {
        [EnterpriseEvaluation] = null,
        [Enterprise] = "NPPR9-FWDCX-D2C8J-H872K-2YT43",
        [Pro] = "W269N-WFGWX-YVC9B-4J6C9-T83GX",
        [Education] = "NW6C2-QMPVW-D7KKK-3GKT6-VCFB2",
    };

    private static readonly string[] PreferredOrder = [EnterpriseEvaluation, Enterprise, Pro];

    /// <summary>The reason this image cannot be installed, or null when it can.</summary>
    public static string? Unsupported(InstallerImage image)
    {
        if (image.Name == Home)
            return "Windows 11 Home is not supported: it has no KMS client setup key and ignores the policies Maldex sets";
        if (!Supported.ContainsKey(image.Name))
            return $"{(image.Name.Length > 0 ? image.Name : $"image {image.Index}")} is not supported; choose Enterprise Evaluation, Enterprise, Pro or Education";
        if (!IsX64(image.Architecture)) return "only x64 images can be installed";
        return null;
    }

    public static EditionChoice? Preferred(IReadOnlyList<InstallerImage> images)
    {
        foreach (var name in PreferredOrder)
        {
            var image = images.FirstOrDefault(i => i.Name == name && Unsupported(i) is null);
            if (image is not null) return ToChoice(image);
        }
        return null;
    }

    public static EditionChoice Choose(IReadOnlyList<InstallerImage> images, int? index)
    {
        if (index is null)
            return Preferred(images) ?? throw new EditionNotSupportedException("no Enterprise or Pro image on this media");
        var image = images.FirstOrDefault(i => i.Index == index)
            ?? throw new EditionNotSupportedException($"the media has no image with index {index}");
        if (Unsupported(image) is { } reason) throw new EditionNotSupportedException(reason);
        return ToChoice(image);
    }

    private static EditionChoice ToChoice(InstallerImage image) => new(image.Index, image.Name, Supported[image.Name]);

    private static bool IsX64(string? architecture) =>
        architecture is not null &&
        (architecture.Equals("x86_64", StringComparison.OrdinalIgnoreCase)
         || architecture.Equals("x64", StringComparison.OrdinalIgnoreCase)
         || architecture.Equals("AMD64", StringComparison.OrdinalIgnoreCase));
}

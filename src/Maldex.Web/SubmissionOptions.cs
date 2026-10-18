namespace Maldex.Web;

/// <summary>Bound from <c>Maldex</c>: the images analysts may choose and the upload size cap.</summary>
public sealed class SubmissionOptions
{
    public const string SectionName = "Maldex";
    public const string DefaultImage = "win10-ltsc-2021";

    /// <summary>Empty after binding means "use the default"; see <see cref="ApplyDefaults"/>. (Config binding appends to non-empty lists, so no initializer.)</summary>
    public List<string> Images { get; set; } = [];

    public long MaxUploadBytes { get; set; } = 268_435_456;

    public void ApplyDefaults()
    {
        Images = Images.Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
        if (Images.Count == 0) Images.Add(DefaultImage);
        if (MaxUploadBytes < 1) MaxUploadBytes = 268_435_456;
    }
}

/// <summary>Bound from <c>Maldex:Egress</c>. An empty list means LiveVpn is unavailable.</summary>
public sealed class EgressOptions
{
    public const string SectionName = "Maldex:Egress";
    public List<string> VpnCountries { get; set; } = [];
}

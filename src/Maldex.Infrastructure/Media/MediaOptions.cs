namespace Maldex.Infrastructure.Media;

/// <summary>Bound from <c>Maldex:Media</c>.</summary>
public sealed class MediaOptions
{
    public const string SectionName = "Maldex:Media";

    /// <summary>Media files live at <c>{Root}/{sha256}.iso</c>, staging files at <c>{Root}/staging/{id:N}.part</c>. Resolved in a PostConfigure.</summary>
    public string Root { get; set; } = ".data/media";
    public long MaxBytes { get; set; } = 16L * 1024 * 1024 * 1024;
    public long ChunkBytes { get; set; } = 16L * 1024 * 1024;
    public long MaxChunkBytes { get; set; } = 64L * 1024 * 1024;
    public bool AllowPrivateFetch { get; set; }
    public TimeSpan AbandonAfter { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan FetchTimeout { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan FetchIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public int MaxRedirects { get; set; } = 5;
    public TimeSpan InspectTimeout { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Looked up on PATH; never resolved against the root.</summary>
    public string WiminfoBinary { get; set; } = "wiminfo";
}

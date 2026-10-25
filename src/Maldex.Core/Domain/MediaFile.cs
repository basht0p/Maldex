namespace Maldex.Core.Domain;

/// <summary>An installation or application image in the media library. Stored once by <see cref="Sha256"/>; never executed on the host.</summary>
public class MediaFile
{
    public Guid Id { get; set; }
    public MediaKind Kind { get; set; }
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public long ReceivedBytes { get; set; }
    public string? Sha256 { get; set; }
    public MediaSource Source { get; set; }
    public string? SourceUrl { get; set; }
    /// <summary>JSON describing what the image holds (for an installer, its editions); null until inspected.</summary>
    public string? Contents { get; set; }
    public MediaStatus Status { get; set; } = MediaStatus.Receiving;
    public string? FailureReason { get; set; }
    public Guid AddedByUserId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

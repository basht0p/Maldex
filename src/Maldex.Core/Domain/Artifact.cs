namespace Maldex.Core.Domain;

public class Artifact
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public ArtifactKind Kind { get; set; }
    public string BlobKey { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }

    /// <summary>For <see cref="ArtifactKind.Dropped"/>: the file's path inside the guest (guest-supplied text, cleaned and truncated).</summary>
    public string? GuestPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

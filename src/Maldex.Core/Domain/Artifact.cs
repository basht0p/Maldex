namespace Maldex.Core.Domain;

public class Artifact
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public ArtifactKind Kind { get; set; }
    public string BlobKey { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

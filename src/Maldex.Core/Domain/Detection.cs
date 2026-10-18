namespace Maldex.Core.Domain;

public class Detection
{
    public long Id { get; set; }
    public Guid TaskId { get; set; }
    public DetectionSource Source { get; set; }
    public string Rule { get; set; } = "";
    public int Severity { get; set; }
    /// <summary>Comma-joined tags; may be empty.</summary>
    public string Tags { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Details { get; set; }
}

namespace Maldex.Core.Domain;

/// <summary>What analysts choose. <see cref="Id"/> is a slug and is the value of <c>AnalysisTask.Image</c>.</summary>
public class Template
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public bool Enabled { get; set; } = true;
    public Guid? CurrentVersionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

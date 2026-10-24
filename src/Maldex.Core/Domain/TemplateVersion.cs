namespace Maldex.Core.Domain;

/// <summary>One sealed, immutable disk of a <see cref="Template"/>.</summary>
public class TemplateVersion
{
    public Guid Id { get; set; }
    public string TemplateId { get; set; } = "";
    public int Number { get; set; }
    public Guid? ParentVersionId { get; set; }
    public TemplateVersionOrigin Origin { get; set; }
    /// <summary>Disk files are <c>base/&lt;FileStem&gt;.qcow2</c> and <c>.vars.fd</c>.</summary>
    public string FileStem { get; set; } = "";
    public string? WindowsEdition { get; set; }
    public string? WindowsBuild { get; set; }
    public bool? OfficePresent { get; set; }
    public string? AgentVersion { get; set; }
    public string? SysmonConfigSha256 { get; set; }
    public string? MitmCaSha256 { get; set; }
    /// <summary>JSON list of media ids.</summary>
    public string SourceMediaIds { get; set; } = "[]";
    public Guid? SealedByUserId { get; set; }
    public DateTimeOffset SealedAt { get; set; }
    public string? Note { get; set; }
    public TemplateVersionStatus Status { get; set; } = TemplateVersionStatus.Ready;
    public long? SizeBytes { get; set; }
}

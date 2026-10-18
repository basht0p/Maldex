namespace Maldex.Core.Domain;

public class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    /// <summary>Null means the system acted.</summary>
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Details { get; set; }
}

namespace Maldex.Core.Domain;

public class BehaviorEvent
{
    public long Id { get; set; }
    public Guid TaskId { get; set; }
    public long? ProcessNodeId { get; set; }
    public int Pid { get; set; }
    public EventType Type { get; set; }
    public DateTimeOffset At { get; set; }
    public string Summary { get; set; } = "";
    /// <summary>JSON text.</summary>
    public string Data { get; set; } = "{}";
}

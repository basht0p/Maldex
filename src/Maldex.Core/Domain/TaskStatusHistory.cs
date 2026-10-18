namespace Maldex.Core.Domain;

public class TaskStatusHistory
{
    public long Id { get; set; }
    public Guid TaskId { get; set; }
    public RunStatus? From { get; set; }
    public RunStatus To { get; set; }
    public DateTimeOffset At { get; set; }
    public string Reason { get; set; } = "";
}

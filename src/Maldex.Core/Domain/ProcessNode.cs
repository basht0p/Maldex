namespace Maldex.Core.Domain;

public class ProcessNode
{
    public long Id { get; set; }
    public Guid TaskId { get; set; }
    public int Pid { get; set; }
    public int ParentPid { get; set; }
    public long? ParentNodeId { get; set; }
    public string ImagePath { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public string? User { get; set; }
    public string? Integrity { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? ExitCode { get; set; }
}

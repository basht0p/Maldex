namespace Maldex.Core.Domain;

/// <summary>One transition of a <see cref="TemplateJob"/>: the state it entered, when, and the job's message then. Ordered by <see cref="Id"/>.</summary>
public class TemplateJobStep
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public TemplateJobState State { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Message { get; set; }
}

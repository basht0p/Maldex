using Maldex.Core.Lifecycle;

namespace Maldex.Core.Domain;

public class AnalysisTask
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string? SampleSha256 { get; set; }
    public string? Url { get; set; }
    public string? OriginalFileName { get; set; }
    public Guid SubmittedByUserId { get; set; }
    public string Image { get; set; } = "";
    /// <summary>The template version the run is bound to when it starts; null for tasks from before templates.</summary>
    public Guid? TemplateVersionId { get; set; }
    public NetworkMode NetworkMode { get; set; }
    public string? ExitCountry { get; set; }
    public int DurationSec { get; set; }
    public bool Interactive { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Queued;
    public Verdict Verdict { get; set; } = Verdict.Unknown;
    public int Attempt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? DeadlineAt { get; set; }
    public DateTimeOffset? StopRequestedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public string? FailureReason { get; set; }
    /// <summary>Warnings the run itself recorded start with this; processing keeps them when it rewrites the list.</summary>
    public const string CaptureWarningPrefix = "capture: ";

    public List<string> ProcessingWarnings { get; set; } = [];
    public byte[] RowVersion { get; set; } = [];
    public List<TaskStatusHistory> StatusHistory { get; set; } = [];

    public Org? Org { get; set; }
    public Sample? Sample { get; set; }
    public AppUser? SubmittedBy { get; set; }

    public void TransitionTo(RunStatus to, string reason, DateTimeOffset at)
    {
        if (!RunStateMachine.CanTransition(Status, to))
            throw new InvalidOperationException($"Illegal transition {Status} -> {to}.");
        StatusHistory.Add(new TaskStatusHistory { TaskId = Id, From = Status, To = to, At = at, Reason = reason });
        Status = to;
        if (RunStateMachine.IsTerminal(to)) CompletedAt = at;
    }
}

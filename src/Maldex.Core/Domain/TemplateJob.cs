using Maldex.Core.Lifecycle;

namespace Maldex.Core.Domain;

/// <summary>An edit or refresh of a <see cref="Template"/>: runs a VM from the base version and seals the result as a new version.</summary>
public class TemplateJob
{
    public Guid Id { get; set; }
    public TemplateJobKind Kind { get; set; }
    public string TemplateId { get; set; } = "";
    public Guid BaseVersionId { get; set; }
    public Guid? ResultVersionId { get; set; }
    public NetworkMode Network { get; set; }
    public TemplateJobState State { get; set; } = TemplateJobState.Queued;
    public string? Message { get; set; }
    public string? FailureReason { get; set; }
    public Guid RequestedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset? ConfiguringSince { get; set; }
    public DateTimeOffset? LastConsoleAt { get; set; }
    public bool PublishOnSuccess { get; set; } = true;
    public string? Note { get; set; }
    public DateTimeOffset? SealRequestedAt { get; set; }
    public DateTimeOffset? CancelRequestedAt { get; set; }
    /// <summary>When the job took a VM slot (before its first VM boots). A non-terminal job with this set counts as busy in <c>VmSlots</c>.</summary>
    public DateTimeOffset? SlotHeldAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public void TransitionTo(TemplateJobState to, string? message, DateTimeOffset at)
    {
        if (!TemplateJobStateMachine.CanTransition(State, to, Kind))
            throw new InvalidOperationException($"Illegal transition {State} -> {to} for a {Kind} job.");
        State = to;
        Message = message;
        if (TemplateJobStateMachine.IsTerminal(to)) EndedAt = at;
    }
}

using System.Linq.Expressions;
using Maldex.Core.Lifecycle;

namespace Maldex.Core.Domain;

/// <summary>An edit, refresh or build of a <see cref="Template"/>: runs a VM from the base version and seals the result as a new version.</summary>
public class TemplateJob
{
    public Guid Id { get; set; }
    public TemplateJobKind Kind { get; set; }
    public string TemplateId { get; set; } = "";
    /// <summary>The version an Edit or Refresh starts from; null for a Build (it starts from installation media).</summary>
    public Guid? BaseVersionId { get; set; }
    /// <summary>A Build's source media ids as a JSON list, the <see cref="MediaKind.OsInstaller"/> first.</summary>
    public string MediaIds { get; set; } = "[]";
    public int? EditionIndex { get; set; }
    public string? EditionName { get; set; }
    /// <summary>For a Build: the template's <c>CurrentVersionId</c> when the job was queued (null for a new or empty template); the publish guard compares against it. Not a foreign key.</summary>
    public Guid? ExpectedCurrentVersionId { get; set; }
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
    /// <summary>The transitions made (one per <see cref="TransitionTo"/>), ordered by id. Not loaded by default: a fresh load holds none, and the steps a save finds here are inserted.</summary>
    public List<TemplateJobStep> Steps { get; set; } = [];

    /// <summary>
    /// When an admin may drive the guest through the console (R22): Configuring and neither a seal nor a cancel requested.
    /// The token route, the tunnel open check and the open tunnel's watch loop all use this one definition.
    /// </summary>
    public static readonly Expression<Func<TemplateJob, bool>> ConsoleOpen =
        j => j.State == TemplateJobState.Configuring && j.SealRequestedAt == null && j.CancelRequestedAt == null;

    public void TransitionTo(TemplateJobState to, string? message, DateTimeOffset at)
    {
        if (!TemplateJobStateMachine.CanTransition(State, to, Kind))
            throw new InvalidOperationException($"Illegal transition {State} -> {to} for a {Kind} job.");
        State = to;
        Message = message;
        if (TemplateJobStateMachine.IsTerminal(to)) EndedAt = at;
        Steps.Add(new TemplateJobStep { JobId = Id, State = to, At = at, Message = message });
    }
}

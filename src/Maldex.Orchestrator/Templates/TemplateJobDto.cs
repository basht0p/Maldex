using Maldex.Core.Domain;
using Maldex.Core.Lifecycle;

namespace Maldex.Orchestrator.Templates;

/// <summary>
/// A template job as the admin API and the hub show it. <see cref="BaseVersionNumber"/> is null for a Build.
/// <see cref="IdleDeadline"/> is when a Configuring job is discarded for want of a console connection (null in every other state).
/// <see cref="SealRequested"/> comes from the server so a refused seal (Defender) shows the button again.
/// <see cref="Steps"/> is the job's transitions in order (empty for a job from before the step history);
/// <see cref="RequestedByName"/> is the requester's display name (null in hub pushes).
/// </summary>
public sealed record TemplateJobDto(Guid Id, string Kind, string TemplateId, int? BaseVersionNumber, int? ResultVersionNumber, string Network,
    string State, string? Message, string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt, DateTimeOffset? IdleDeadline,
    string? Edition, bool HasProvisionLog, bool SealRequested, IReadOnlyList<TemplateJobStepDto> Steps, string? RequestedByName = null)
{
    /// <summary>
    /// The wire form of <paramref name="job"/>; the version numbers, the steps (ordered here by id) and the requester's name are looked up
    /// by the caller. No <paramref name="steps"/>: none (the job's own <see cref="TemplateJob.Steps"/> are never used, as a loaded job holds only unsaved ones).
    /// </summary>
    public static TemplateJobDto From(TemplateJob job, int? baseVersionNumber, int? resultVersionNumber, TimeSpan configureIdleTimeout, bool hasProvisionLog = false,
        IReadOnlyList<TemplateJobStep>? steps = null, string? requestedByName = null) =>
        new(job.Id, job.Kind.ToString(), job.TemplateId, baseVersionNumber, resultVersionNumber, job.Network.ToString(), job.State.ToString(),
            job.Message, job.FailureReason, job.CreatedAt, job.EndedAt, IdleDeadlineOf(job, configureIdleTimeout),
            job.EditionName, hasProvisionLog, job.SealRequestedAt is not null,
            steps is null ? [] : [.. steps.OrderBy(s => s.Id).Select(s => new TemplateJobStepDto(s.State.ToString(), s.At, s.Message))],
            requestedByName);

    /// <summary><c>max(ConfiguringSince, LastConsoleAt) + timeout</c> while Configuring; the same rule the coordinator discards by.</summary>
    public static DateTimeOffset? IdleDeadlineOf(TemplateJob job, TimeSpan configureIdleTimeout) =>
        job.State == TemplateJobState.Configuring && job.ConfiguringSince is { } since
            ? TemplateJobStateMachine.IdleDeadline(since, job.LastConsoleAt, configureIdleTimeout)
            : null;
}

/// <summary>One transition of a template job: the state it entered, when, and the message it said then.</summary>
public sealed record TemplateJobStepDto(string State, DateTimeOffset At, string? Message);

/// <summary>Pushes job updates to the clients watching the job (implemented over SignalR by Maldex.Web).</summary>
public interface ITemplateJobPublisher
{
    Task PublishAsync(TemplateJobDto job, CancellationToken ct);
}

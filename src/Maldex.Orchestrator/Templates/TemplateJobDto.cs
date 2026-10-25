using Maldex.Core.Domain;
using Maldex.Core.Lifecycle;

namespace Maldex.Orchestrator.Templates;

/// <summary>
/// A template job as the admin API and the hub show it. <see cref="IdleDeadline"/> is when a Configuring job is discarded
/// for want of a console connection (null in every other state).
/// </summary>
public sealed record TemplateJobDto(Guid Id, string Kind, string TemplateId, int BaseVersionNumber, int? ResultVersionNumber, string Network,
    string State, string? Message, string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt, DateTimeOffset? IdleDeadline)
{
    /// <summary>The wire form of <paramref name="job"/>; the version numbers are looked up by the caller.</summary>
    public static TemplateJobDto From(TemplateJob job, int baseVersionNumber, int? resultVersionNumber, TimeSpan configureIdleTimeout) =>
        new(job.Id, job.Kind.ToString(), job.TemplateId, baseVersionNumber, resultVersionNumber, job.Network.ToString(), job.State.ToString(),
            job.Message, job.FailureReason, job.CreatedAt, job.EndedAt, IdleDeadlineOf(job, configureIdleTimeout));

    /// <summary><c>max(ConfiguringSince, LastConsoleAt) + timeout</c> while Configuring; the same rule the coordinator discards by.</summary>
    public static DateTimeOffset? IdleDeadlineOf(TemplateJob job, TimeSpan configureIdleTimeout) =>
        job.State == TemplateJobState.Configuring && job.ConfiguringSince is { } since
            ? TemplateJobStateMachine.IdleDeadline(since, job.LastConsoleAt, configureIdleTimeout)
            : null;
}

/// <summary>Pushes job updates to the clients watching the job (implemented over SignalR by Maldex.Web).</summary>
public interface ITemplateJobPublisher
{
    Task PublishAsync(TemplateJobDto job, CancellationToken ct);
}

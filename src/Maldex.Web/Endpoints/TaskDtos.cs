using Maldex.Core.Domain;
using Maldex.Web.Templates;

namespace Maldex.Web.Endpoints;

public sealed record TaskDto(
    Guid Id,
    Guid OrgId,
    string OrgName,
    string? SampleSha256,
    string? Url,
    string? OriginalFileName,
    string Image,
    NetworkMode NetworkMode,
    string? ExitCountry,
    int DurationSec,
    bool Interactive,
    RunStatus Status,
    Verdict Verdict,
    int QueuePosition,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? DeadlineAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    IReadOnlyList<string> ProcessingWarnings,
    TaskTemplateDto? Template,
    string? SubmittedByName = null,
    int? RanSec = null)
{
    /// <summary>The single entity-to-DTO mapping. <paramref name="queuePosition"/> is 0 unless the task is queued. <paramref name="template"/> is null for tasks that predate templates.</summary>
    public static TaskDto From(AnalysisTask t, string orgName, int queuePosition, TaskTemplateDto? template = null) => new(
        t.Id, t.OrgId, orgName, t.SampleSha256, t.Url, t.OriginalFileName, t.Image, t.NetworkMode, t.ExitCountry,
        t.DurationSec, t.Interactive, t.Status, t.Verdict, queuePosition, t.CreatedAt, t.StartedAt, t.DeadlineAt,
        t.CompletedAt, t.FailureReason, t.ProcessingWarnings, template, t.SubmittedBy?.DisplayName, RanSecOf(t));

    /// <summary>The run's length: start to <c>EndedAt ?? CompletedAt</c>; null when either end is missing.</summary>
    private static int? RanSecOf(AnalysisTask t) =>
        t.StartedAt is { } s && (t.EndedAt ?? t.CompletedAt) is { } e ? (int)Math.Min(int.MaxValue, Math.Max(0, (e - s).TotalSeconds)) : null;
}

public sealed record TaskListDto(IReadOnlyList<TaskDto> Items, int Total);

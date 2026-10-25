using Maldex.Core.Domain;

namespace Maldex.Web.Endpoints;

/// <summary>The in-app task report. Strings may come from the guest and are plain text: the UI encodes them.</summary>
public sealed record TaskReportDto(
    TaskDto Task, string? SubmittedByName, string? Sha1, string? Md5, long? SampleSize, string? FileType, string? Mime,
    string? TemplateSummary,
    IReadOnlyList<StatusStepDto> StatusHistory, IReadOnlyList<DetectionDto> Detections, IReadOnlyList<ReportProcessDto> Processes,
    IReadOnlyList<NetworkFlowDto> NetworkFlows, IReadOnlyList<DnsRowDto> Dns, IReadOnlyList<HttpRowDto> Http,
    IReadOnlyList<DroppedFileDto> DroppedFiles, IReadOnlyList<ScreenshotRefDto> Screenshots,
    SamePreviousDto SamePrevious, TruncatedDto Truncated);

public sealed record StatusStepDto(RunStatus? From, RunStatus To, DateTimeOffset At, string Reason);
public sealed record DetectionDto(DetectionSource Source, string Rule, int Severity, IReadOnlyList<string> Tags, string Target, string? Details);
public sealed record ReportProcessDto(long Id, long? ParentId, int Pid, string ImagePath, string CommandLine, string? User, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int Depth);
public sealed record NetworkFlowDto(string Proto, string Src, string Dst, int DstPort, long Bytes, DateTimeOffset StartedAt);
public sealed record DnsRowDto(DateTimeOffset At, int Pid, string Query, IReadOnlyList<string> Answers);
public sealed record HttpRowDto(long Id, string Method, string Url, string Host, int? Status, string ReqHeaders, string RespHeaders, bool HasBody);
public sealed record DroppedFileDto(Guid ArtifactId, string? GuestPath, string Sha256, long Size, DateTimeOffset CreatedAt);
public sealed record ScreenshotRefDto(Guid ArtifactId, DateTimeOffset At);
public sealed record SamePreviousDto(int Count, SamePreviousLatestDto? Latest);
public sealed record SamePreviousLatestDto(Guid TaskId, Verdict Verdict, DateTimeOffset CreatedAt);
public sealed record TruncatedDto(bool Processes, bool NetworkFlows, bool Dns, bool Http);

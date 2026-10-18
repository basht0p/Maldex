using Maldex.Core.Domain;

namespace Maldex.Core.Live;

public sealed record TaskStatusUpdate(Guid TaskId, RunStatus Status, Verdict Verdict, DateTimeOffset? DeadlineAt, string? Reason);

public sealed record ProcessNodeDto(long Id, int Pid, long? ParentId, string ImagePath, string CommandLine, string? User, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

public sealed record LiveEventDto(long Id, EventType Type, DateTimeOffset At, int Pid, long? ProcessId, string Summary, string DataJson);

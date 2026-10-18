using Maldex.Core.Live;

namespace Maldex.Core.Abstractions;

public interface ITaskEventPublisher
{
    Task PublishStatusAsync(TaskStatusUpdate update, CancellationToken ct);
    Task PublishProcessAsync(Guid taskId, ProcessNodeDto process, CancellationToken ct);
    Task PublishEventsAsync(Guid taskId, IReadOnlyList<LiveEventDto> events, CancellationToken ct);
}

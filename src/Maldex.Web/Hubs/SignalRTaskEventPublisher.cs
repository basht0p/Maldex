using Maldex.Core.Abstractions;
using Maldex.Core.Live;
using Microsoft.AspNetCore.SignalR;

namespace Maldex.Web.Hubs;

/// <summary>Pushes live task data to the clients that joined the task's hub group (see <see cref="TaskHub"/>).</summary>
public sealed class SignalRTaskEventPublisher(IHubContext<TaskHub> hub) : ITaskEventPublisher
{
    public Task PublishStatusAsync(TaskStatusUpdate update, CancellationToken ct) =>
        Group(update.TaskId).SendAsync("status", update, ct);

    public Task PublishProcessAsync(Guid taskId, ProcessNodeDto process, CancellationToken ct) =>
        Group(taskId).SendAsync("process", process, ct);

    public Task PublishEventsAsync(Guid taskId, IReadOnlyList<LiveEventDto> events, CancellationToken ct) =>
        Group(taskId).SendAsync("events", events, ct);

    private IClientProxy Group(Guid taskId) => hub.Clients.Group(TaskHub.GroupName(taskId));
}

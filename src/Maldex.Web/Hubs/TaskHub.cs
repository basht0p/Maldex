using Maldex.Data;
using Maldex.Web.Audit;
using Maldex.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Web.Hubs;

/// <summary>
/// Live updates for analysis tasks, at <c>/hubs/tasks</c>. A client joins the group <c>task:{id}</c> and receives:
/// <c>"status"(TaskStatusUpdate)</c>, <c>"process"(ProcessNodeDto)</c> (a node is sent again when it exits) and
/// <c>"events"(LiveEventDto[])</c>, pushed by <see cref="SignalRTaskEventPublisher"/> right after the data is saved.
///
/// <para><b>Client rejoin protocol.</b> The hub only pushes what happens after a client has joined; history comes
/// from REST. A client that opens the task page, or reconnects after a drop, must do this, in this order, so that
/// it ends up with every event exactly once:</para>
/// <list type="number">
/// <item><description><c>JoinTask(taskId)</c> <b>first</b>. Anything saved from now on is pushed to this connection.</description></item>
/// <item><description><b>Buffer</b> every live callback (<c>"status"</c>, <c>"process"</c>, <c>"events"</c>) instead of rendering it.</description></item>
/// <item><description>Fetch <c>GET /api/tasks/{id}/processes</c> and <c>GET /api/tasks/{id}/events?afterId=0</c>.
/// Events come back in ascending id order, at most 1000 per call: keep calling with <c>afterId</c> set to the last
/// id received until a call returns fewer than <c>take</c> events.</description></item>
/// <item><description>Apply the fetched data, then the buffer, skipping every event whose id is less than or equal
/// to the highest id applied so far. Keep applying that same rule to later live batches. The buffer and the
/// backlog overlap (an event saved between the join and the fetch is in both), so the skip is what removes the
/// duplicates; the join-before-fetch order is what removes the gaps. Processes are upserted by id.</description></item>
/// </list>
/// </summary>
[Authorize(Policy = AuthSetup.AnalystPolicy)]
public sealed class TaskHub(MaldexDbContext db, CurrentUser user, AuditWriter audit) : Hub
{
    public static string GroupName(Guid taskId) => $"task:{taskId}";

    public async Task JoinTask(Guid taskId)
    {
        var ct = Context.ConnectionAborted;
        // The per-invocation hub scope is not the request scope, so CurrentUserMiddleware has not run for it.
        await user.ResolveAsync(Context.User!, ct);
        if (!await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == taskId, ct))
            throw new HubException("Task not found.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(taskId), ct);
        await audit.WriteAsync("session.join", taskId.ToString(), null, ct);
    }

    public Task LeaveTask(Guid taskId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(taskId), Context.ConnectionAborted);
}

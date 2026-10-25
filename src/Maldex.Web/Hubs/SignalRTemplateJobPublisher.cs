using Maldex.Orchestrator.Templates;
using Microsoft.AspNetCore.SignalR;

namespace Maldex.Web.Hubs;

/// <summary>Pushes template job updates to the admins who joined the job's hub group (see <see cref="TaskHub.JoinTemplateJob"/>).</summary>
public sealed class SignalRTemplateJobPublisher(IHubContext<TaskHub> hub) : ITemplateJobPublisher
{
    public Task PublishAsync(TemplateJobDto job, CancellationToken ct) =>
        hub.Clients.Group(TaskHub.TemplateJobGroupName(job.Id)).SendAsync("templateJob", job, ct);
}

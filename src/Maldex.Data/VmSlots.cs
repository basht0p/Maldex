using Maldex.Core.Domain;
using Maldex.Core.Lifecycle;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Data;

/// <summary>
/// The single count of VMs in use; analyses and template jobs share <c>MaxConcurrentVms</c>. A template job holds its slot
/// from <see cref="TemplateJob.SlotHeldAt"/> (set just before its first VM boots) until it is terminal.
/// </summary>
public static class VmSlots
{
    public static readonly RunStatus[] TaskStates = [RunStatus.Preparing, RunStatus.Running, RunStatus.Collecting];

    public static readonly IReadOnlyList<TemplateJobState> Terminal = TemplateJobStateMachine.TerminalStates;

    public static async Task<int> BusyAsync(MaldexDbContext db, CancellationToken ct)
    {
        var tasks = await db.Tasks.AsNoTracking().CountAsync(t => TaskStates.Contains(t.Status), ct).ConfigureAwait(false);
        var jobs = await db.TemplateJobs.AsNoTracking().CountAsync(j => j.SlotHeldAt != null && !Terminal.Contains(j.State), ct).ConfigureAwait(false);
        return tasks + jobs;
    }
}

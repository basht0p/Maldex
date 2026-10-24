using Maldex.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Data;

/// <summary>The single count of VMs in use; analyses and template jobs share <c>MaxConcurrentVms</c>.</summary>
public static class VmSlots
{
    private static readonly RunStatus[] TaskStates = [RunStatus.Preparing, RunStatus.Running, RunStatus.Collecting];

    // Preparing jobs only copy disks and hold no VM.
    private static readonly TemplateJobState[] JobStates = [TemplateJobState.Configuring, TemplateJobState.Sealing, TemplateJobState.Verifying];

    public static async Task<int> BusyAsync(MaldexDbContext db, CancellationToken ct)
    {
        var tasks = await db.Tasks.AsNoTracking().CountAsync(t => TaskStates.Contains(t.Status), ct).ConfigureAwait(false);
        var jobs = await db.TemplateJobs.AsNoTracking().CountAsync(j => JobStates.Contains(j.State), ct).ConfigureAwait(false);
        return tasks + jobs;
    }
}

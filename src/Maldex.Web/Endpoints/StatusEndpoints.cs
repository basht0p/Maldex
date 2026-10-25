using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Core.Health;
using Maldex.Data;
using Maldex.Infrastructure.Libvirt;
using Maldex.Infrastructure.Media;
using Maldex.Infrastructure.Storage;
using Maldex.Orchestrator;
using Maldex.Web.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Maldex.Web.Endpoints;

public sealed record SystemStatusDto(VmSlotStatusDto VmSlot, int QueueDepth, int RunningTasks, int ActiveJobs,
    IReadOnlyList<DiskStatusDto> Disks, IReadOnlyList<WorkerStatusDto> Workers, IReadOnlyList<RecentFailureDto> RecentFailures, DateTimeOffset GeneratedAt);
public sealed record VmSlotStatusDto(int Capacity, int Busy, IReadOnlyList<SlotHolderDto> Holders);
/// <summary>Kind is "task" or "job".</summary>
public sealed record SlotHolderDto(string Kind, Guid Id, string Label, DateTimeOffset Since);
/// <summary>Name is "data", "vm" or "media"; Path is the directory actually measured.</summary>
public sealed record DiskStatusDto(string Name, string Path, long FreeBytes, long TotalBytes);
public sealed record WorkerStatusDto(string Name, DateTimeOffset? LastTickAt, int ExpectedIntervalSec, bool Stalled);
public sealed record RecentFailureDto(string Kind, Guid Id, string Label, string? Reason, DateTimeOffset At);

public static class StatusEndpoints
{
    private const int RecentFailureCount = 10;

    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/status", async (MaldexDbContext db, WorkerHealth health, IDiskUsageProbe disks, TimeProvider time,
            IOptions<OrchestratorOptions> orchestrator, IOptions<ArtifactStoreOptions> artifacts, IOptions<LibvirtOptions> libvirt,
            IOptions<MediaOptions> media, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();

            // Same predicates as VmSlots.BusyAsync, so the holders always add up to Busy.
            var taskHolders = await db.Tasks.AsNoTracking().Where(t => VmSlots.TaskStates.Contains(t.Status))
                .Select(t => new { t.Id, t.OriginalFileName, t.Url, t.StartedAt, t.CreatedAt }).ToListAsync(ct);
            var jobHolders = await db.TemplateJobs.AsNoTracking().Where(j => j.SlotHeldAt != null && !VmSlots.Terminal.Contains(j.State))
                .Select(j => new { j.Id, j.Kind, j.TemplateId, j.SlotHeldAt }).ToListAsync(ct);
            var holders = taskHolders.Select(t => new SlotHolderDto("task", t.Id, t.OriginalFileName ?? t.Url ?? "task", t.StartedAt ?? t.CreatedAt))
                .Concat(jobHolders.Select(j => new SlotHolderDto("job", j.Id, $"{j.Kind} {j.TemplateId}", j.SlotHeldAt!.Value)))
                .OrderBy(h => h.Since).ToList();

            var queueDepth = await db.Tasks.AsNoTracking().CountAsync(t => t.Status == RunStatus.Queued, ct);
            var runningTasks = taskHolders.Count;
            var activeJobs = await db.TemplateJobs.AsNoTracking().CountAsync(j => !VmSlots.Terminal.Contains(j.State), ct);
            var busy = await VmSlots.BusyAsync(db, ct);

            var failedTasks = await db.Tasks.AsNoTracking().Where(t => t.Status == RunStatus.Failed)
                .OrderByDescending(t => t.EndedAt ?? t.CompletedAt ?? t.CreatedAt).Take(RecentFailureCount)
                .Select(t => new { t.Id, t.OriginalFileName, t.Url, t.FailureReason, At = t.EndedAt ?? t.CompletedAt ?? t.CreatedAt }).ToListAsync(ct);
            var failedJobs = await db.TemplateJobs.AsNoTracking().Where(j => j.State == TemplateJobState.Failed)
                .OrderByDescending(j => j.EndedAt ?? j.CreatedAt).Take(RecentFailureCount)
                .Select(j => new { j.Id, j.Kind, j.TemplateId, j.FailureReason, At = j.EndedAt ?? j.CreatedAt }).ToListAsync(ct);
            var failures = failedTasks.Select(t => new RecentFailureDto("task", t.Id, t.OriginalFileName ?? t.Url ?? "task", t.FailureReason, t.At))
                .Concat(failedJobs.Select(j => new RecentFailureDto("job", j.Id, $"{j.Kind} {j.TemplateId}", j.FailureReason, j.At)))
                .OrderByDescending(f => f.At).Take(RecentFailureCount).ToList();

            // Each root is resolved the way its owner resolves it (FileSystemArtifactStore, LibvirtHypervisor.BaseRoot, MediaStore).
            var diskStatus = new List<DiskStatusDto>
            {
                Disk("data", artifacts.Value.Root, disks),
                Disk("vm", libvirt.Value.BaseImageDir, disks),
                Disk("media", media.Value.Root, disks),
            };

            var workers = health.Snapshot()
                .Select(w => new WorkerStatusDto(w.Name, w.LastTickAt, (int)Math.Ceiling(w.ExpectedInterval.TotalSeconds), w.Stalled)).ToList();

            return new SystemStatusDto(new VmSlotStatusDto(orchestrator.Value.MaxConcurrentVms, busy, holders),
                queueDepth, runningTasks, activeJobs, diskStatus, workers, failures, now);
        }).RequireAuthorization(AuthSetup.AdminPolicy);

        return app;
    }

    private static DiskStatusDto Disk(string name, string root, IDiskUsageProbe probe)
    {
        var u = probe.Usage(Path.GetFullPath(root));
        return new DiskStatusDto(name, u.Path, u.FreeBytes, u.TotalBytes);
    }
}

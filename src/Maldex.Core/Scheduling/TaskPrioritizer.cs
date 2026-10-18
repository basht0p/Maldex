namespace Maldex.Core.Scheduling;

public record QueuedTaskInfo(Guid Id, bool Interactive, DateTimeOffset CreatedAt);

public static class TaskPrioritizer
{
    // Single ordering shared by SelectNext and PositionOf so they cannot disagree.
    // Interactive first, then oldest first, then Id as a deterministic tiebreak.
    private static IEnumerable<QueuedTaskInfo> Ordered(IEnumerable<QueuedTaskInfo> queued) =>
        queued
            .OrderByDescending(t => t.Interactive)
            .ThenBy(t => t.CreatedAt)
            .ThenBy(t => t.Id);

    public static IReadOnlyList<Guid> SelectNext(IEnumerable<QueuedTaskInfo> queued, int freeSlots)
    {
        if (freeSlots <= 0)
            return Array.Empty<Guid>();

        return Ordered(queued).Take(freeSlots).Select(t => t.Id).ToList();
    }

    public static int PositionOf(Guid id, IEnumerable<QueuedTaskInfo> queued)
    {
        var position = 0;
        foreach (var task in Ordered(queued))
        {
            position++;
            if (task.Id == id)
                return position;
        }

        return 0;
    }
}

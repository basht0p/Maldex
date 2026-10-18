using Maldex.Core.Scheduling;
using Xunit;

namespace Maldex.Core.Tests.Scheduling;

public class TaskPrioritizerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:000000000000}");

    [Fact]
    public void InteractiveBeforeAutomated_ThenFifo()
    {
        var autoOld = new QueuedTaskInfo(G(1), false, T0);
        var autoNew = new QueuedTaskInfo(G(2), false, T0.AddSeconds(5));
        var interactiveNew = new QueuedTaskInfo(G(3), true, T0.AddSeconds(10));
        var interactiveOld = new QueuedTaskInfo(G(4), true, T0.AddSeconds(1));

        var order = TaskPrioritizer.SelectNext(new[] { autoNew, interactiveNew, autoOld, interactiveOld }, 10);

        Assert.Equal(new[] { G(4), G(3), G(1), G(2) }, order);
    }

    [Fact]
    public void RespectsFreeSlots()
    {
        var queued = Enumerable.Range(1, 5)
            .Select(i => new QueuedTaskInfo(G(i), false, T0.AddSeconds(i)))
            .ToList();

        var picked = TaskPrioritizer.SelectNext(queued, 2);

        Assert.Equal(new[] { G(1), G(2) }, picked);
        Assert.Equal(5, TaskPrioritizer.SelectNext(queued, 99).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ZeroOrNegativeSlots_ReturnsEmpty(int freeSlots)
    {
        var queued = new[] { new QueuedTaskInfo(G(1), true, T0) };

        Assert.Empty(TaskPrioritizer.SelectNext(queued, freeSlots));
    }

    [Fact]
    public void TiesOnCreatedAt_BreakById()
    {
        var high = new QueuedTaskInfo(G(9), false, T0);
        var low = new QueuedTaskInfo(G(2), false, T0);

        var first = TaskPrioritizer.SelectNext(new[] { high, low }, 2);
        var second = TaskPrioritizer.SelectNext(new[] { low, high }, 2);

        Assert.Equal(new[] { G(2), G(9) }, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void PositionOf_MatchesSelectOrder()
    {
        var queued = new[]
        {
            new QueuedTaskInfo(G(1), false, T0),
            new QueuedTaskInfo(G(2), true, T0.AddSeconds(3)),
            new QueuedTaskInfo(G(3), false, T0.AddSeconds(1)),
            new QueuedTaskInfo(G(4), true, T0.AddSeconds(2)),
            new QueuedTaskInfo(G(5), false, T0),
        };

        var order = TaskPrioritizer.SelectNext(queued, int.MaxValue);

        for (var i = 0; i < order.Count; i++)
            Assert.Equal(i + 1, TaskPrioritizer.PositionOf(order[i], queued));

        Assert.Equal(0, TaskPrioritizer.PositionOf(G(999), queued));
        Assert.Equal(0, TaskPrioritizer.PositionOf(G(1), Array.Empty<QueuedTaskInfo>()));
    }
}

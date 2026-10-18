using Xunit;
using Maldex.Core.Domain;
using Maldex.Core.Lifecycle;
using static Maldex.Core.Domain.RunStatus;

namespace Maldex.Core.Tests;

public class RunStateMachineTests
{
    private static readonly (RunStatus, RunStatus)[] AllowedPairs =
    [
        (Queued, Preparing), (Queued, Cancelled),
        (Preparing, Running), (Preparing, Failed), (Preparing, Cancelled),
        (Running, Collecting), (Running, Failed),
        (Collecting, Processing), (Collecting, Failed),
        (Processing, Completed), (Processing, Failed),
    ];

    [Theory]
    [InlineData(Queued, Preparing)] [InlineData(Queued, Cancelled)]
    [InlineData(Preparing, Running)] [InlineData(Preparing, Failed)] [InlineData(Preparing, Cancelled)]
    [InlineData(Running, Collecting)] [InlineData(Running, Failed)]
    [InlineData(Collecting, Processing)] [InlineData(Collecting, Failed)]
    [InlineData(Processing, Completed)] [InlineData(Processing, Failed)]
    public void Allowed(RunStatus f, RunStatus t) => Assert.True(RunStateMachine.CanTransition(f, t));

    [Fact]
    public void AllOtherPairs_AreRejected()
    {
        var all = Enum.GetValues<RunStatus>();
        Assert.Equal(8, all.Length);
        var allowed = 0;
        foreach (var f in all)
            foreach (var t in all)
            {
                var expected = AllowedPairs.Contains((f, t));
                Assert.Equal(expected, RunStateMachine.CanTransition(f, t));
                if (expected) allowed++;
            }
        Assert.Equal(11, allowed);
    }

    [Fact]
    public void Terminal_HasNoExits()
    {
        foreach (var f in new[] { Completed, Failed, Cancelled })
        {
            Assert.True(RunStateMachine.IsTerminal(f));
            foreach (var t in Enum.GetValues<RunStatus>())
                Assert.False(RunStateMachine.CanTransition(f, t));
        }
        Assert.False(RunStateMachine.IsTerminal(Running));
    }

    [Fact]
    public void TransitionTo_AppendsHistory_AndSetsCompletedAtOnTerminal()
    {
        var task = new AnalysisTask { Id = Guid.NewGuid() };
        Assert.Equal(Queued, task.Status);
        Assert.Equal(Verdict.Unknown, task.Verdict);
        Assert.Equal(0, task.Attempt);
        var t0 = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

        task.TransitionTo(Preparing, "picked up", t0);
        Assert.Equal(Preparing, task.Status);
        Assert.Null(task.CompletedAt);
        var h = Assert.Single(task.StatusHistory);
        Assert.Equal(task.Id, h.TaskId);
        Assert.Equal(Queued, h.From);
        Assert.Equal(Preparing, h.To);
        Assert.Equal(t0, h.At);
        Assert.Equal("picked up", h.Reason);

        task.TransitionTo(Failed, "boom", t0.AddMinutes(1));
        Assert.Equal(2, task.StatusHistory.Count);
        Assert.Equal(t0.AddMinutes(1), task.CompletedAt);
    }

    [Fact]
    public void TransitionTo_Invalid_Throws_AndLeavesStatusUnchanged()
    {
        var task = new AnalysisTask { Id = Guid.NewGuid() };
        Assert.Throws<InvalidOperationException>(() => task.TransitionTo(Completed, "skip", DateTimeOffset.UnixEpoch));
        Assert.Equal(Queued, task.Status);
        Assert.Empty(task.StatusHistory);
        Assert.Null(task.CompletedAt);
    }
}

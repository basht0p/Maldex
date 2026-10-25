using Maldex.Core.Domain;
using Maldex.Core.Lifecycle;
using Xunit;
using static Maldex.Core.Domain.TemplateJobState;

namespace Maldex.Core.Tests.Lifecycle;

public class TemplateJobStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly (TemplateJobState, TemplateJobState)[] AllowedPairs =
    [
        (Queued, Preparing), (Queued, Cancelled), (Queued, Failed),
        (Preparing, Configuring), (Preparing, Sealing), (Preparing, Failed), (Preparing, Cancelled),
        (Configuring, Sealing), (Configuring, Cancelled), (Configuring, Failed),
        (Sealing, Verifying), (Sealing, Failed), (Sealing, Cancelled),
        (Verifying, Published), (Verifying, Sealed), (Verifying, Failed), (Verifying, Cancelled),
    ];

    public static TheoryData<TemplateJobState, TemplateJobState> Allowed()
    {
        var d = new TheoryData<TemplateJobState, TemplateJobState>();
        foreach (var (f, t) in AllowedPairs) d.Add(f, t);
        return d;
    }

    [Theory]
    [MemberData(nameof(Allowed))]
    public void Allowed_Pairs(TemplateJobState f, TemplateJobState t) => Assert.True(TemplateJobStateMachine.CanTransition(f, t));

    [Fact]
    public void AllOtherPairs_AreRejected()
    {
        foreach (var f in Enum.GetValues<TemplateJobState>())
            foreach (var t in Enum.GetValues<TemplateJobState>())
                if (!AllowedPairs.Contains((f, t)))
                    Assert.False(TemplateJobStateMachine.CanTransition(f, t), $"{f} -> {t} must be illegal");
    }

    [Fact]
    public void TerminalStates_AreExactlyPublishedSealedFailedCancelled()
    {
        TemplateJobState[] terminal = [Published, Sealed, Failed, Cancelled];
        foreach (var s in Enum.GetValues<TemplateJobState>())
            Assert.Equal(terminal.Contains(s), TemplateJobStateMachine.IsTerminal(s));
    }

    [Fact]
    public void TheStateLists_PartitionTheStates_FromOneDefinition()
    {
        Assert.Equal([Published, Sealed, Failed, Cancelled], TemplateJobStateMachine.TerminalStates);
        Assert.Equal([Queued, Preparing, Configuring, Sealing, Verifying], TemplateJobStateMachine.ActiveStates);
        Assert.Equal([Preparing, Configuring, Sealing, Verifying], TemplateJobStateMachine.StartedStates);
        Assert.Equal(Enum.GetValues<TemplateJobState>().Order(), TemplateJobStateMachine.TerminalStates.Concat(TemplateJobStateMachine.ActiveStates).Order());
        Assert.All(TemplateJobStateMachine.ActiveStates, s => Assert.False(TemplateJobStateMachine.IsTerminal(s)));
    }

    [Fact]
    public void IdleDeadline_CountsFromTheLaterOfConfiguringAndTheLastConsole()
    {
        var timeout = TimeSpan.FromHours(2);
        Assert.Equal(Now + timeout, TemplateJobStateMachine.IdleDeadline(Now, null, timeout));
        Assert.Equal(Now + timeout, TemplateJobStateMachine.IdleDeadline(Now, Now.AddMinutes(-5), timeout)); // a console before Configuring
        Assert.Equal(Now.AddMinutes(30) + timeout, TemplateJobStateMachine.IdleDeadline(Now, Now.AddMinutes(30), timeout));
    }

    [Fact]
    public void Preparing_GoesToConfiguringOnlyForEdit_AndToSealingOnlyForRefresh()
    {
        Assert.True(TemplateJobStateMachine.CanTransition(Preparing, Configuring, TemplateJobKind.Edit));
        Assert.False(TemplateJobStateMachine.CanTransition(Preparing, Sealing, TemplateJobKind.Edit));
        Assert.True(TemplateJobStateMachine.CanTransition(Preparing, Sealing, TemplateJobKind.Refresh));
        Assert.False(TemplateJobStateMachine.CanTransition(Preparing, Configuring, TemplateJobKind.Refresh));
    }

    [Fact]
    public void TransitionTo_MovesState_SetsMessage_AndStampsEndOnTerminal()
    {
        var job = new TemplateJob { Kind = TemplateJobKind.Edit };
        job.TransitionTo(Preparing, "copying", Now);
        Assert.Equal(Preparing, job.State);
        Assert.Equal("copying", job.Message);
        Assert.Null(job.EndedAt);
        job.TransitionTo(Failed, "boom", Now.AddMinutes(1));
        Assert.Equal(Now.AddMinutes(1), job.EndedAt);
    }

    [Fact]
    public void TransitionTo_IllegalMove_Throws()
    {
        var job = new TemplateJob { Kind = TemplateJobKind.Edit };
        Assert.Throws<InvalidOperationException>(() => job.TransitionTo(Verifying, null, Now));
        Assert.Equal(Queued, job.State);
    }

    [Fact]
    public void TransitionTo_RefreshCannotConfigure_Throws()
    {
        var job = new TemplateJob { Kind = TemplateJobKind.Refresh, State = Preparing };
        Assert.Throws<InvalidOperationException>(() => job.TransitionTo(Configuring, null, Now));
    }
}

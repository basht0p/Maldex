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
        (Preparing, Installing),
        (Installing, Provisioning), (Installing, Failed), (Installing, Cancelled),
        (Provisioning, Configuring), (Provisioning, Failed), (Provisioning, Cancelled),
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
        Assert.Equal([Queued, Preparing, Configuring, Sealing, Verifying, Installing, Provisioning], TemplateJobStateMachine.ActiveStates);
        Assert.Equal([Preparing, Configuring, Sealing, Verifying, Installing, Provisioning], TemplateJobStateMachine.StartedStates);
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
    public void ActiveAndStartedStates_IncludeInstallingAndProvisioning()
    {
        Assert.Contains(Installing, TemplateJobStateMachine.ActiveStates);
        Assert.Contains(Provisioning, TemplateJobStateMachine.ActiveStates);
        Assert.Contains(Installing, TemplateJobStateMachine.StartedStates);
        Assert.Contains(Provisioning, TemplateJobStateMachine.StartedStates);
    }

    [Fact]
    public void Build_WalksPreparingInstallingProvisioningConfiguring_AndCannotSkipInstalling()
    {
        const TemplateJobKind build = TemplateJobKind.Build;
        Assert.True(TemplateJobStateMachine.CanTransition(Queued, Preparing, build));
        Assert.True(TemplateJobStateMachine.CanTransition(Preparing, Installing, build));
        Assert.True(TemplateJobStateMachine.CanTransition(Installing, Provisioning, build));
        Assert.True(TemplateJobStateMachine.CanTransition(Provisioning, Configuring, build));
        Assert.True(TemplateJobStateMachine.CanTransition(Configuring, Sealing, build));
        Assert.False(TemplateJobStateMachine.CanTransition(Preparing, Configuring, build));
        Assert.False(TemplateJobStateMachine.CanTransition(Preparing, Sealing, build));
    }

    [Theory]
    [InlineData(TemplateJobKind.Edit)]
    [InlineData(TemplateJobKind.Refresh)]
    public void OnlyABuildInstalls(TemplateJobKind kind) =>
        Assert.False(TemplateJobStateMachine.CanTransition(Preparing, Installing, kind));

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
    public void TransitionTo_appends_a_step_with_state_time_and_message()
    {
        var job = new TemplateJob { Id = Guid.NewGuid(), Kind = TemplateJobKind.Edit };
        job.TransitionTo(Preparing, "copying", Now);
        job.TransitionTo(Configuring, null, Now.AddMinutes(2));

        Assert.Collection(job.Steps,
            s =>
            {
                Assert.Equal((job.Id, Preparing, Now, "copying"), (s.JobId, s.State, s.At, s.Message));
            },
            s =>
            {
                Assert.Equal((job.Id, Configuring, Now.AddMinutes(2), (string?)null), (s.JobId, s.State, s.At, s.Message));
            });
    }

    [Fact]
    public void Illegal_transition_appends_nothing()
    {
        var job = new TemplateJob { Kind = TemplateJobKind.Edit };
        Assert.Throws<InvalidOperationException>(() => job.TransitionTo(Verifying, "nope", Now));
        Assert.Empty(job.Steps);
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

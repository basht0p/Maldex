using System.Collections.Frozen;
using Maldex.Core.Domain;
using static Maldex.Core.Domain.TemplateJobState;

namespace Maldex.Core.Lifecycle;

public static class TemplateJobStateMachine
{
    private static readonly FrozenDictionary<TemplateJobState, FrozenSet<TemplateJobState>> Allowed =
        new Dictionary<TemplateJobState, FrozenSet<TemplateJobState>>
        {
            [Queued] = new[] { Preparing, Cancelled, Failed }.ToFrozenSet(),
            [Preparing] = new[] { Configuring, Sealing, Installing, Failed, Cancelled }.ToFrozenSet(),
            [Installing] = new[] { Provisioning, Failed, Cancelled }.ToFrozenSet(),
            [Provisioning] = new[] { Configuring, Failed, Cancelled }.ToFrozenSet(),
            [Configuring] = new[] { Sealing, Cancelled, Failed }.ToFrozenSet(),
            [Sealing] = new[] { Verifying, Failed, Cancelled }.ToFrozenSet(),
            [Verifying] = new[] { Published, Sealed, Failed, Cancelled }.ToFrozenSet(),
            [Published] = FrozenSet<TemplateJobState>.Empty,
            [Sealed] = FrozenSet<TemplateJobState>.Empty,
            [Failed] = FrozenSet<TemplateJobState>.Empty,
            [Cancelled] = FrozenSet<TemplateJobState>.Empty,
        }.ToFrozenDictionary();

    /// <summary>Whether the move is legal for some kind of job.</summary>
    public static bool CanTransition(TemplateJobState from, TemplateJobState to) =>
        Allowed.TryGetValue(from, out var set) && set.Contains(to);

    /// <summary>As the kind-less overload, and after preparing an Edit job configures, a Refresh job goes straight to sealing and a Build job installs.</summary>
    public static bool CanTransition(TemplateJobState from, TemplateJobState to, TemplateJobKind kind)
    {
        if (!CanTransition(from, to)) return false;
        if (from != Preparing) return true;
        if (to == Configuring) return kind == TemplateJobKind.Edit;
        if (to == Sealing) return kind == TemplateJobKind.Refresh;
        if (to == Installing) return kind == TemplateJobKind.Build;
        return true;
    }

    /// <summary>The terminal states: the one list <see cref="IsTerminal"/>, the slot count and the one-active-job index filter follow.</summary>
    public static readonly IReadOnlyList<TemplateJobState> TerminalStates = [Published, Sealed, Failed, Cancelled];

    public static bool IsTerminal(TemplateJobState s) => TerminalSet.Contains(s);

    private static readonly FrozenSet<TemplateJobState> TerminalSet = TerminalStates.ToFrozenSet();

    /// <summary>The non-terminal states: the one list the endpoints and the coordinator count "a job is active" by.</summary>
    public static readonly IReadOnlyList<TemplateJobState> ActiveStates = [.. Enum.GetValues<TemplateJobState>().Where(s => !TerminalSet.Contains(s))];

    /// <summary>The states of a job that has started (past Queued) and not ended: what a restart interrupts.</summary>
    public static readonly IReadOnlyList<TemplateJobState> StartedStates = [.. ActiveStates.Where(s => s != Queued)];

    /// <summary>
    /// When a Configuring job is discarded for want of a console: <c>max(configuringSince, lastConsoleAt) + timeout</c>. The one rule
    /// the coordinator discards by and the API shows.
    /// </summary>
    public static DateTimeOffset IdleDeadline(DateTimeOffset configuringSince, DateTimeOffset? lastConsoleAt, TimeSpan timeout) =>
        (lastConsoleAt is { } console && console > configuringSince ? console : configuringSince) + timeout;
}

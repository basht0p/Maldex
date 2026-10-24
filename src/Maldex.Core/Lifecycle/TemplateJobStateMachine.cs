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
            [Preparing] = new[] { Configuring, Sealing, Failed, Cancelled }.ToFrozenSet(),
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

    /// <summary>As the kind-less overload, and an Edit job configures after preparing while a Refresh job goes straight to sealing.</summary>
    public static bool CanTransition(TemplateJobState from, TemplateJobState to, TemplateJobKind kind)
    {
        if (!CanTransition(from, to)) return false;
        if (from != Preparing) return true;
        if (to == Configuring) return kind == TemplateJobKind.Edit;
        if (to == Sealing) return kind == TemplateJobKind.Refresh;
        return true;
    }

    public static bool IsTerminal(TemplateJobState s) => s is Published or Sealed or Failed or Cancelled;
}

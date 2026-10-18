using System.Collections.Frozen;
using Maldex.Core.Domain;
using static Maldex.Core.Domain.RunStatus;

namespace Maldex.Core.Lifecycle;

public static class RunStateMachine
{
    private static readonly FrozenDictionary<RunStatus, FrozenSet<RunStatus>> Allowed =
        new Dictionary<RunStatus, FrozenSet<RunStatus>>
        {
            [Queued] = new[] { Preparing, Cancelled }.ToFrozenSet(),
            [Preparing] = new[] { Running, Failed, Cancelled }.ToFrozenSet(),
            [Running] = new[] { Collecting, Failed }.ToFrozenSet(),
            [Collecting] = new[] { Processing, Failed }.ToFrozenSet(),
            [Processing] = new[] { Completed, Failed }.ToFrozenSet(),
            [Completed] = FrozenSet<RunStatus>.Empty,
            [Failed] = FrozenSet<RunStatus>.Empty,
            [Cancelled] = FrozenSet<RunStatus>.Empty,
        }.ToFrozenDictionary();

    public static bool CanTransition(RunStatus from, RunStatus to) =>
        Allowed.TryGetValue(from, out var set) && set.Contains(to);

    public static bool IsTerminal(RunStatus s) => s is Completed or Failed or Cancelled;
}

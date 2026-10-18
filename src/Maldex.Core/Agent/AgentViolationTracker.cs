namespace Maldex.Core.Agent;

public sealed class AgentViolationTracker(int limit = 10)
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public string? LastReason { get; private set; }

    /// <summary>Records a violation; returns true once the limit is reached.</summary>
    public bool Record(string reason)
    {
        LastReason = reason;
        return Interlocked.Increment(ref _count) >= limit;
    }
}

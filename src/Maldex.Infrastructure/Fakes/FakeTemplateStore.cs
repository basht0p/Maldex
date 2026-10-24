using Maldex.Core.Abstractions;

namespace Maldex.Infrastructure.Fakes;

/// <summary>
/// Fake mode has no disks: every version "exists" (unless a test lists it in <see cref="Missing"/>), has no known size, and
/// copies, seals and deletes are only recorded.
/// </summary>
public sealed class FakeTemplateStore : ITemplateStore
{
    /// <summary>Stems a test wants to have no disk.</summary>
    public HashSet<string> Missing { get; } = [];
    public List<(string From, string To)> WorkingCopies { get; } = [];
    public List<string> Sealed { get; } = [];
    public List<string> Deleted { get; } = [];

    public bool Exists(string fileStem)
    {
        lock (Missing) return !Missing.Contains(fileStem);
    }

    public long? SizeOf(string fileStem) => null;

    public Task CreateWorkingCopyAsync(string fromStem, string toStem, CancellationToken ct)
    {
        lock (WorkingCopies) WorkingCopies.Add((fromStem, toStem));
        return Task.CompletedTask;
    }

    public Task SealAsync(string fileStem, CancellationToken ct)
    {
        lock (Sealed) Sealed.Add(fileStem);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string fileStem, CancellationToken ct)
    {
        lock (Deleted) Deleted.Add(fileStem);
        return Task.CompletedTask;
    }
}

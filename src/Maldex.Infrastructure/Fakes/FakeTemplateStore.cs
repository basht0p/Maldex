using Maldex.Core.Abstractions;

namespace Maldex.Infrastructure.Fakes;

/// <summary>Fake mode has no disks: every version "exists", has no known size, and deleting is a no-op; copies and seals are only recorded.</summary>
public sealed class FakeTemplateStore : ITemplateStore
{
    public bool Exists(string fileStem) => true;
    public long? SizeOf(string fileStem) => null;
    public List<(string From, string To)> WorkingCopies { get; } = [];
    public List<string> Sealed { get; } = [];

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

    public Task DeleteAsync(string fileStem, CancellationToken ct) => Task.CompletedTask;
}

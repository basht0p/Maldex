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

    public List<(string Stem, long DiskBytes, string VarsTemplatePath)> Blanks { get; } = [];

    public bool Exists(string fileStem)
    {
        lock (Missing) return !Missing.Contains(fileStem);
    }

    public long? SizeOf(string fileStem) => null;

    /// <summary>Runs when a working copy is made (a test can change the world between the bundle load and the install).</summary>
    public Action? OnCreateWorkingCopy { get; set; }

    public Task CreateWorkingCopyAsync(string fromStem, string toStem, CancellationToken ct)
    {
        OnCreateWorkingCopy?.Invoke();
        lock (WorkingCopies) WorkingCopies.Add((fromStem, toStem));
        return Task.CompletedTask;
    }

    public Task CreateBlankAsync(string stem, long diskBytes, string varsTemplatePath, CancellationToken ct)
    {
        lock (Blanks)
        {
            if (Blanks.Any(b => b.Stem == stem)) throw new InvalidOperationException($"Template file stem '{stem}' already exists.");
            Blanks.Add((stem, diskBytes, varsTemplatePath));
        }
        lock (Missing) Missing.Remove(stem); // the stem now exists
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

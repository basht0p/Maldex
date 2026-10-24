using Maldex.Core.Abstractions;

namespace Maldex.Infrastructure.Fakes;

/// <summary>Fake mode has no disks: every version "exists", has no known size, and deleting is a no-op.</summary>
public sealed class FakeTemplateStore : ITemplateStore
{
    public bool Exists(string fileStem) => true;
    public long? SizeOf(string fileStem) => null;
    public Task DeleteAsync(string fileStem, CancellationToken ct) => Task.CompletedTask;
}

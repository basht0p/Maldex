using Maldex.Core.Abstractions;

namespace Maldex.Infrastructure.Fakes;

/// <summary>Fake mode has no VNC console, so the live-view tunnel has nothing to connect to.</summary>
public sealed class FakeConsoleLocator : IVmConsoleLocator
{
    public Task<VncEndpoint?> GetAsync(VmHandle vm, CancellationToken ct) => Task.FromResult<VncEndpoint?>(null);
}

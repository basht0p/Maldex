using System.Collections.Concurrent;
using Maldex.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Fakes;

public sealed class FakeHypervisor(IOptions<FakeHypervisorOptions> options) : IHypervisor
{
    private readonly ConcurrentDictionary<string, VmHandle> _running = new();
    private int _starts;

    public Task<VmHandle> StartAsync(VmSpec spec, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _starts) <= options.Value.FailStarts)
            throw new VmStartException("Fake hypervisor configured to fail this start.");
        var handle = VmHandle.For(spec.TaskId);
        _running[handle.Name] = handle;
        return Task.FromResult(handle);
    }

    public Task DestroyAsync(VmHandle handle, CancellationToken ct)
    {
        _running.TryRemove(handle.Name, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VmHandle>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VmHandle>>(_running.Values.ToArray());
}

using System.Collections.Concurrent;
using Maldex.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Fakes;

public sealed class FakeHypervisor : IHypervisor
{
    private readonly ConcurrentDictionary<string, VmHandle> _running = new();
    private readonly IOptions<FakeHypervisorOptions> options;
    private int _starts;

    /// <param name="guest">When given, a VM whose fake guest handles <c>power_off</c> stops being listed, like a VM that shut itself down.</param>
    public FakeHypervisor(IOptions<FakeHypervisorOptions> options, FakeGuest? guest = null)
    {
        this.options = options;
        if (guest is not null) guest.PoweredOff += taskId => _running.TryRemove(VmHandle.For(taskId).Name, out _);
    }

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

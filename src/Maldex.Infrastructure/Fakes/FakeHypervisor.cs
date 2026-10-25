using System.Collections.Concurrent;
using Maldex.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Fakes;

public sealed class FakeHypervisor : IHypervisor
{
    // Listed VMs. A VM that powered itself off stays listed (like libvirt's run directory) until DestroyAsync, but is no longer running.
    private readonly ConcurrentDictionary<string, VmHandle> _running = new();
    private readonly ConcurrentDictionary<string, byte> _off = new();
    private readonly IOptions<FakeHypervisorOptions> options;
    private readonly ConcurrentDictionary<string, VmSpec> _specs = new();
    private readonly ConcurrentQueue<(VmHandle Vm, string Key)> _keys = new();
    private int _starts;
    private int _maxRunning;

    /// <summary>The most VMs that were ever running at the same time (checked at each start).</summary>
    public int MaxRunning => Volatile.Read(ref _maxRunning);

    /// <param name="guest">When given, a VM whose fake guest handles <c>power_off</c> stops being listed, like a VM that shut itself down.</param>
    public FakeHypervisor(IOptions<FakeHypervisorOptions> options, FakeGuest? guest = null)
    {
        this.options = options;
        if (guest is not null) guest.PoweredOff += taskId => _off[VmHandle.For(taskId).Name] = 0;
    }

    /// <summary>The spec the VM was last started with (CD-ROMs, boot flag), or null when it was never started.</summary>
    public VmSpec? LastSpec(VmHandle vm) => _specs.TryGetValue(vm.Name, out var s) ? s : null;

    /// <summary>Every key pressed through <see cref="SendKeyAsync"/>, in order.</summary>
    public IReadOnlyList<(VmHandle Vm, string Key)> KeysSent => _keys.ToArray();

    public Task<VmHandle> StartAsync(VmSpec spec, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _starts) <= options.Value.FailStarts)
            throw new VmStartException("Fake hypervisor configured to fail this start.");
        var handle = VmHandle.For(spec.TaskId);
        _off.TryRemove(handle.Name, out _);
        _specs[handle.Name] = spec;
        _running[handle.Name] = handle;
        var running = _running.Keys.Count(name => !_off.ContainsKey(name));
        int seen;
        while (running > (seen = Volatile.Read(ref _maxRunning)) && Interlocked.CompareExchange(ref _maxRunning, running, seen) != seen) { }
        return Task.FromResult(handle);
    }

    public Task DestroyAsync(VmHandle handle, CancellationToken ct)
    {
        _running.TryRemove(handle.Name, out _);
        _off.TryRemove(handle.Name, out _);
        return Task.CompletedTask;
    }

    public Task<bool> IsRunningAsync(VmHandle vm, CancellationToken ct) =>
        Task.FromResult(_running.ContainsKey(vm.Name) && !_off.ContainsKey(vm.Name));

    public Task<IReadOnlyList<VmHandle>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VmHandle>>(_running.Values.ToArray());

    public Task SendKeyAsync(VmHandle vm, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _keys.Enqueue((vm, key));
        return Task.CompletedTask;
    }
}

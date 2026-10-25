namespace Maldex.Core.Abstractions;

public sealed record VmSpec(Guid TaskId, string DiskStem, int MemoryMiB, int VCpus, string NetworkId, bool Writable = false);
// Writable: boot base/<DiskStem>.qcow2 and its .vars.fd directly (a template working copy) instead of a throwaway overlay.

public sealed record VmHandle(Guid TaskId, string Name)
{
    public static VmHandle For(Guid taskId) => new(taskId, $"maldex-{taskId:N}");
}

public sealed class VmStartException : Exception
{
    public VmStartException(string message) : base(message) { }
    public VmStartException(string message, Exception? inner) : base(message, inner) { }
}

public interface IHypervisor
{
    Task<VmHandle> StartAsync(VmSpec spec, CancellationToken ct);

    /// <summary>Idempotent.</summary>
    Task DestroyAsync(VmHandle handle, CancellationToken ct);

    /// <summary>
    /// True while the guest is executing (running, paused, shutting down); false once it has shut off, crashed or no longer exists.
    /// Unlike <see cref="ListAsync"/>, a VM that is off but not yet destroyed (its run directory still exists) is not running.
    /// </summary>
    Task<bool> IsRunningAsync(VmHandle vm, CancellationToken ct);

    Task<IReadOnlyList<VmHandle>> ListAsync(CancellationToken ct);
}

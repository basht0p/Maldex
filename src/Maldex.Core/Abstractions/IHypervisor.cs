namespace Maldex.Core.Abstractions;

public sealed record VmSpec(Guid TaskId, string DiskStem, int MemoryMiB, int VCpus, string NetworkId, bool Writable = false,
    IReadOnlyList<string>? CdRoms = null, bool BootFromCdRom = false);
// Writable: boot base/<DiskStem>.qcow2 and its .vars.fd directly (a template working copy) instead of a throwaway overlay.
// CdRoms (at most 4, absolute paths of existing .iso files, Writable boots only) are attached read-only on SATA in list order;
// BootFromCdRom makes the first one the first boot device (an installer boot).

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

    /// <summary>Presses one key in the guest console. <paramref name="key"/> is a libvirt key name matching <c>^KEY_[A-Z0-9_]{1,20}\z</c> (e.g. KEY_ENTER).</summary>
    Task SendKeyAsync(VmHandle vm, string key, CancellationToken ct);
}

namespace Maldex.Core.Abstractions;

public sealed record VmSpec(Guid TaskId, string Image, int MemoryMiB, int VCpus, string NetworkId);

public sealed record VmHandle(Guid TaskId, string Name)
{
    public static VmHandle For(Guid taskId) => new(taskId, $"maldex-{taskId:N}");
}

public sealed class VmStartException(string message) : Exception(message);

public interface IHypervisor
{
    Task<VmHandle> StartAsync(VmSpec spec, CancellationToken ct);

    /// <summary>Idempotent.</summary>
    Task DestroyAsync(VmHandle handle, CancellationToken ct);

    Task<IReadOnlyList<VmHandle>> ListAsync(CancellationToken ct);
}

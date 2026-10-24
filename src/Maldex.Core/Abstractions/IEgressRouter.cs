using Maldex.Core.Domain;

namespace Maldex.Core.Abstractions;

public sealed record NetworkLease(Guid TaskId, string NetworkId);

public sealed class EgressUnavailableException(string message) : Exception(message);

public interface IEgressRouter
{
    /// <exception cref="EgressUnavailableException">The requested mode is not available.</exception>
    Task<NetworkLease> CreateAsync(Guid taskId, NetworkMode mode, string? exitCountry, CancellationToken ct);

    /// <summary>Idempotent.</summary>
    Task ReleaseAsync(NetworkLease lease, CancellationToken ct);

    /// <summary>
    /// Cuts a live lease off from all egress while the VM keeps running (and the agent channel with it): the run namespace
    /// gets the no-egress ruleset and, for a <see cref="NetworkMode.Direct"/> lease, loses its uplink route and the host
    /// forward/NAT table. Idempotent; <see cref="ReleaseAsync"/> still tears everything down.
    /// </summary>
    Task IsolateAsync(NetworkLease lease, CancellationToken ct);

    Task<IReadOnlyList<NetworkLease>> ListAsync(CancellationToken ct);
}

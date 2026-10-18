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

    Task<IReadOnlyList<NetworkLease>> ListAsync(CancellationToken ct);
}

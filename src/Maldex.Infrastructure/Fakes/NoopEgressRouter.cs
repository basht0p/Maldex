using System.Collections.Concurrent;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Fakes;

public sealed class NoopEgressRouter(IOptions<FakeEgressOptions> options) : IEgressRouter
{
    private readonly ConcurrentDictionary<Guid, NetworkLease> _leases = new();

    public Task<NetworkLease> CreateAsync(Guid taskId, NetworkMode mode, string? exitCountry, CancellationToken ct)
    {
        if (mode is NetworkMode.LiveVpn or NetworkMode.LiveTor && !options.Value.LiveAvailable)
            throw new EgressUnavailableException("live egress is not available in fake mode");
        var lease = _leases.GetOrAdd(taskId, id => new NetworkLease(id, $"fake-net-{id:N}"));
        return Task.FromResult(lease);
    }

    public Task ReleaseAsync(NetworkLease lease, CancellationToken ct)
    {
        _leases.TryRemove(lease.TaskId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NetworkLease>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NetworkLease>>(_leases.Values.ToArray());
}

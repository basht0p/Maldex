using System.Collections.Concurrent;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Fakes;

public sealed class NoopEgressRouter(IOptions<FakeEgressOptions> options, FakeGuest? guest = null) : IEgressRouter
{
    private readonly ConcurrentDictionary<Guid, NetworkLease> _leases = new();

    public Task<NetworkLease> CreateAsync(Guid taskId, NetworkMode mode, string? exitCountry, CancellationToken ct)
    {
        if (mode is NetworkMode.LiveVpn or NetworkMode.LiveTor && !options.Value.LiveAvailable)
            throw new EgressUnavailableException("live egress is not available in fake mode");
        // A fake LiveVpn guest "sits behind" the configured exit, so its self-test echo is that IP; every other mode has no internet.
        guest?.SetEchoFor(taskId, mode == NetworkMode.LiveVpn ? options.Value.LiveEgressIp : null);
        var lease = _leases.GetOrAdd(taskId, id => new NetworkLease(id, $"fake-net-{id:N}"));
        return Task.FromResult(lease);
    }

    public Task ReleaseAsync(NetworkLease lease, CancellationToken ct)
    {
        _leases.TryRemove(lease.TaskId, out _);
        guest?.SetEchoFor(lease.TaskId, null);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NetworkLease>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NetworkLease>>(_leases.Values.ToArray());
}

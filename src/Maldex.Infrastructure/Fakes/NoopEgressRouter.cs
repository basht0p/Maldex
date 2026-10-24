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
        // A fake LiveVpn guest "sits behind" the configured exit, so its self-test echo is that IP; a Simulated one gets INetSim's
        // page; None has no internet.
        guest?.SetModeFor(taskId, mode);
        guest?.SetEchoFor(taskId, mode switch
        {
            NetworkMode.LiveVpn => options.Value.LiveEgressIp,
            NetworkMode.Simulated => FakeGuest.InetSimEchoPage,
            NetworkMode.Direct => "", // the probe URL answered (204, empty body)
            _ => null,
        });
        var lease = _leases.GetOrAdd(taskId, id => new NetworkLease(id, $"fake-net-{id:N}"));
        return Task.FromResult(lease);
    }

    public Task ReleaseAsync(NetworkLease lease, CancellationToken ct)
    {
        _leases.TryRemove(lease.TaskId, out _);
        guest?.SetEchoFor(lease.TaskId, null);
        guest?.SetModeFor(lease.TaskId, null);
        return Task.CompletedTask;
    }

    public Task IsolateAsync(NetworkLease lease, CancellationToken ct)
    {
        guest?.SetModeFor(lease.TaskId, NetworkMode.None);
        guest?.SetEchoFor(lease.TaskId, null);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NetworkLease>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NetworkLease>>(_leases.Values.ToArray());
}

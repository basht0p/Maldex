using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Net;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Fakes;

/// <summary>Fake mode has no real subnets: every run is reported as 10.213.0.1 (gateway) / 10.213.0.2 (guest).</summary>
public sealed class FakeRunNetworkInfo(FakeGuest? guest = null) : IRunNetworkInfo
{
    // The mode is the one the fake router was asked for (tracked per lease), so the coordinator's mode check passes.
    public Task<RunNetworkInfo?> GetAsync(NetworkLease lease, CancellationToken ct) =>
        Task.FromResult<RunNetworkInfo?>(new RunNetworkInfo("10.213.0.1", "10.213.0.2", guest?.ModeFor(lease.TaskId) ?? NetworkMode.None));
}

/// <summary>Fake LiveVpn exit: <see cref="FakeEgressOptions.LiveEgressIp"/>, which the fake guest echoes back.</summary>
public sealed class FakeExpectedEgress(IOptions<FakeEgressOptions> options) : IExpectedEgress
{
    public Task<string?> GetAsync(NetworkMode mode, string? exitCountry, CancellationToken ct) =>
        Task.FromResult(mode == NetworkMode.LiveVpn ? options.Value.LiveEgressIp : null);
}

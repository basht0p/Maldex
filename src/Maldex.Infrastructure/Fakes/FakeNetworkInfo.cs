using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Net;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Fakes;

/// <summary>Fake mode has no real subnets: every run is reported as 10.213.0.1 (gateway) / 10.213.0.2 (guest).</summary>
public sealed class FakeRunNetworkInfo : IRunNetworkInfo
{
    // The fake egress router does not track the mode per lease; consumers use the task's own mode.
    public Task<RunNetworkInfo?> GetAsync(NetworkLease lease, CancellationToken ct) =>
        Task.FromResult<RunNetworkInfo?>(new RunNetworkInfo("10.213.0.1", "10.213.0.2", NetworkMode.None));
}

/// <summary>Fake LiveVpn exit: <see cref="FakeEgressOptions.LiveEgressIp"/>, which the fake guest echoes back.</summary>
public sealed class FakeExpectedEgress(IOptions<FakeEgressOptions> options) : IExpectedEgress
{
    public Task<string?> GetAsync(NetworkMode mode, string? exitCountry, CancellationToken ct) =>
        Task.FromResult(mode == NetworkMode.LiveVpn ? options.Value.LiveEgressIp : null);
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Net;

/// <summary>Addressing of one run's network, as the router allocated it.</summary>
public sealed record RunNetworkInfo(string GatewayIp, string GuestIp, NetworkMode Mode);

/// <summary>Read-only view of a run's network lease. The router owns allocation; this only reports it.</summary>
public interface IRunNetworkInfo
{
    /// <summary>Null when the lease is unknown or unreadable (callers must treat that as "cannot verify").</summary>
    Task<RunNetworkInfo?> GetAsync(NetworkLease lease, CancellationToken ct);
}

/// <summary>Reads <c>{RunDir}/net/{namespace}/lease.json</c>, written by <see cref="NetnsEgressRouter"/>.</summary>
public sealed class LeaseFileRunNetworkInfo(IOptions<NetOptions> options) : IRunNetworkInfo
{
    private sealed record LeaseFile(int Index, string Mode);

    public async Task<RunNetworkInfo?> GetAsync(NetworkLease lease, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetFullPath(options.Value.RunDir), "net", RunNetNames.For(lease.TaskId).Namespace, "lease.json");
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var file = JsonSerializer.Deserialize<LeaseFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (file is null || !Enum.TryParse<NetworkMode>(file.Mode, ignoreCase: false, out var mode) || !Enum.IsDefined(mode)) return null;
            var subnet = RunSubnet.For(file.Index);
            return new RunNetworkInfo(subnet.GatewayIp, subnet.GuestIp, mode);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>The VPN exit IP the self-test must see from a LiveVpn guest.</summary>
public interface IExpectedEgress
{
    /// <summary>Null when it cannot be determined (the self-test then fails).</summary>
    Task<string?> GetAsync(NetworkMode mode, string? exitCountry, CancellationToken ct);
}

/// <summary>Takes the IP from the <c># MaldexEgressIp = x.x.x.x</c> comment of <c>{WireGuardConfigDir}/{country}.conf</c>.</summary>
public sealed class WireGuardExpectedEgress(IOptions<NetOptions> options) : IExpectedEgress
{
    public async Task<string?> GetAsync(NetworkMode mode, string? exitCountry, CancellationToken ct)
    {
        if (mode != NetworkMode.LiveVpn || exitCountry is null || !Regex.IsMatch(exitCountry, "^[A-Za-z0-9_-]{1,32}$")) return null;
        try
        {
            var text = await File.ReadAllTextAsync(Path.Combine(options.Value.WireGuardConfigDir, exitCountry + ".conf"), ct).ConfigureAwait(false);
            return WireGuardConf.Parse(text).EgressIp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EgressUnavailableException)
        {
            return null;
        }
    }
}

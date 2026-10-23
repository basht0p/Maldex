using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Maldex.Core.Agent;

namespace Maldex.Agent.Probes;

/// <summary>Runs the host's isolation self-test from inside the guest.</summary>
public interface ISelfTestProbes
{
    Task<SelfTestResultMessage> RunAsync(SelfTestCommand command, CancellationToken ct);
}

/// <summary>One of the guest's IP addresses, and whether its interface has an IPv4 default gateway.</summary>
public sealed record GuestAddress(string Address, bool HasDefaultGateway);

/// <summary>
/// First waits (up to 20 s) until an interface has an IPv4 address and a default gateway, i.e. the guest got its DHCP lease,
/// and reports those addresses so the host can check the guest really sits on the run's network. Then
/// TCP-connects to every port of every target (all ports of a target in parallel, 1.5 s each) and fetches the echo URL
/// (5 s, body cut to 256 characters). A target is reachable if any port accepted a connection. Probing never throws:
/// any error counts as "not reachable" / "no echo".
/// </summary>
public sealed class SelfTestProbes : ISelfTestProbes
{
    public const int MaxEchoChars = 256;
    private const int MaxTargets = 64;
    private const int MaxPortsPerTarget = 64;
    private const int MaxEchoBytes = 4096;
    private const int MaxConcurrentConnects = 64;
    public const int MaxAddresses = 16;

    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _echoTimeout;
    private readonly Func<string, int, CancellationToken, Task> _connect;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(MaxConcurrentConnects);
    private readonly Func<IReadOnlyList<GuestAddress>> _addresses;
    private readonly TimeSpan _addressWait;
    private readonly TimeSpan _addressPoll;

    /// <param name="connect">Test seam: a TCP connect that completes when the connection is accepted and throws otherwise.</param>
    /// <param name="addresses">Test seam: the guest's current addresses (default: <see cref="ReadInterfaceAddresses"/>).</param>
    public SelfTestProbes(TimeSpan? connectTimeout = null, TimeSpan? echoTimeout = null, Func<string, int, CancellationToken, Task>? connect = null, TimeProvider? time = null,
        Func<IReadOnlyList<GuestAddress>>? addresses = null, TimeSpan? addressWait = null, TimeSpan? addressPoll = null)
    {
        _addresses = addresses ?? ReadInterfaceAddresses;
        _addressWait = addressWait ?? TimeSpan.FromSeconds(20);
        _addressPoll = addressPoll ?? TimeSpan.FromMilliseconds(500);
        _connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(1500);
        _echoTimeout = echoTimeout ?? TimeSpan.FromSeconds(5);
        _connect = connect ?? TcpConnectAsync;
        _time = time ?? TimeProvider.System;
    }

    public async Task<SelfTestResultMessage> RunAsync(SelfTestCommand command, CancellationToken ct)
    {
        var addresses = await WaitForLeaseAsync(ct).ConfigureAwait(false);
        var targets = (command.Targets ?? []).Take(MaxTargets).ToList();
        var probes = await Task.WhenAll(targets.Select(t => ProbeTargetAsync(t, ct))).ConfigureAwait(false);
        var echo = await EchoAsync(command.EchoUrl, ct).ConfigureAwait(false);
        return new SelfTestResultMessage(_time.GetUtcNow(), command.CommandId, probes, echo, addresses);
    }

    /// <summary>
    /// Polls until some IPv4 address sits on an interface with a default gateway (then reports exactly those) or the wait ends
    /// (then reports every non-loopback address it saw, so the host's failure says what the guest had). Never throws.
    /// </summary>
    private async Task<IReadOnlyList<string>> WaitForLeaseAsync(CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + _addressWait;
        IReadOnlyList<GuestAddress> seen = [];
        while (true)
        {
            try { seen = _addresses(); }
            catch (Exception e) when (e is NetworkInformationException or InvalidOperationException or IOException or UnauthorizedAccessException) { seen = []; }
            var leased = seen.Where(a => a.HasDefaultGateway && IPAddress.TryParse(a.Address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork).ToList();
            if (leased.Count > 0) return Cap(leased);
            if (_time.GetUtcNow() >= deadline) return Cap(seen);
            await Task.Delay(_addressPoll, _time, ct).ConfigureAwait(false);
        }

        static IReadOnlyList<string> Cap(IEnumerable<GuestAddress> a) =>
            a.Select(x => x.Address).Where(x => IPAddress.TryParse(x, out _)).Distinct().Take(MaxAddresses).ToList();
    }

    /// <summary>Every non-loopback unicast address of every interface that is up (IPv6 scope ids dropped).</summary>
    public static IReadOnlyList<GuestAddress> ReadInterfaceAddresses()
    {
        var result = new List<GuestAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = nic.GetIPProperties();
            var gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var u in props.UnicastAddresses)
            {
                if (IPAddress.IsLoopback(u.Address)) continue;
                var ip = u.Address.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(u.Address.GetAddressBytes()) : u.Address;
                result.Add(new GuestAddress(ip.ToString(), gateway));
            }
        }
        return result;
    }

    private async Task<ProbeResult> ProbeTargetAsync(ProbeTarget target, CancellationToken ct)
    {
        var ports = (target.Ports ?? []).Where(p => p is >= 1 and <= 65535).Distinct().Take(MaxPortsPerTarget).ToList();
        if (string.IsNullOrWhiteSpace(target.Host) || ports.Count == 0) return new ProbeResult(target.Name, false);
        var outcomes = await Task.WhenAll(ports.Select(p => TryConnectAsync(target.Host, p, ct))).ConfigureAwait(false);
        return new ProbeResult(target.Name, outcomes.Any(o => o));
    }

    private async Task<bool> TryConnectAsync(string host, int port, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false); // bounds concurrency across all targets and ports
        try
        {
            using var timeout = new CancellationTokenSource(_connectTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await _connect(host, port, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task TcpConnectAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
    }

    private async Task<string?> EchoAsync(string? url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        using var timeout = new CancellationTokenSource(_echoTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = _echoTimeout };
            using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            var buffer = new byte[MaxEchoBytes];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await body.ReadAsync(buffer.AsMemory(read), linked.Token).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            if (text.Length <= MaxEchoChars) return text;
            var cut = char.IsHighSurrogate(text[MaxEchoChars - 1]) ? MaxEchoChars - 1 : MaxEchoChars;
            return text[..cut];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

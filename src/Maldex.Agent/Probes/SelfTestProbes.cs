using System.Net;
using System.Net.Sockets;
using System.Text;
using Maldex.Core.Agent;

namespace Maldex.Agent.Probes;

/// <summary>Runs the host's isolation self-test from inside the guest.</summary>
public interface ISelfTestProbes
{
    Task<SelfTestResultMessage> RunAsync(SelfTestCommand command, CancellationToken ct);
}

/// <summary>
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

    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _echoTimeout;
    private readonly Func<string, int, CancellationToken, Task> _connect;
    private readonly TimeProvider _time;

    /// <param name="connect">Test seam: a TCP connect that completes when the connection is accepted and throws otherwise.</param>
    public SelfTestProbes(TimeSpan? connectTimeout = null, TimeSpan? echoTimeout = null, Func<string, int, CancellationToken, Task>? connect = null, TimeProvider? time = null)
    {
        _connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(1500);
        _echoTimeout = echoTimeout ?? TimeSpan.FromSeconds(5);
        _connect = connect ?? TcpConnectAsync;
        _time = time ?? TimeProvider.System;
    }

    public async Task<SelfTestResultMessage> RunAsync(SelfTestCommand command, CancellationToken ct)
    {
        var targets = (command.Targets ?? []).Take(MaxTargets).ToList();
        var probes = await Task.WhenAll(targets.Select(t => ProbeTargetAsync(t, ct))).ConfigureAwait(false);
        var echo = await EchoAsync(command.EchoUrl, ct).ConfigureAwait(false);
        return new SelfTestResultMessage(_time.GetUtcNow(), command.CommandId, probes, echo);
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
        using var timeout = new CancellationTokenSource(_connectTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
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

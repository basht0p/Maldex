using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Maldex.Core.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Media;

public sealed record FetchResult(long Bytes, string Sha256, string? SuggestedFileName);

public sealed class MediaFetchException(string message) : Exception(message);

/// <summary>
/// Downloads installer media from a URL without letting the request reach private, loopback or link-local
/// destinations. The address check happens in the connect callback and the checked address is the one connected to,
/// so a DNS answer cannot change between check and use. Redirects are followed by hand and re-checked.
/// </summary>
public sealed class SafeHttpFetcher
{
    private readonly MediaOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SafeHttpFetcher> _log;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPAddress, bool> _isRefused;

    public SafeHttpFetcher(IOptions<MediaOptions> options, TimeProvider time, ILogger<SafeHttpFetcher> log,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, Func<IPAddress, bool>? isRefused = null)
    {
        _options = options.Value;
        _time = time;
        _log = log;
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        _isRefused = isRefused ?? (ip => UrlFetchPolicy.IsRefused(ip, _options.AllowPrivateFetch, HostAddresses()));
    }

    public async Task<FetchResult> FetchAsync(Uri url, Stream destination, Func<long, Task> progress, CancellationToken ct)
    {
        if (UrlFetchPolicy.RefuseUrl(url) is { } bad) throw new MediaFetchException($"refused: {bad}");

        using var deadline = new CancellationTokenSource(_options.FetchTimeout, _time);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            return await FetchCoreAsync(url, destination, progress, both.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MediaFetchException("the download timed out");
        }
    }

    private async Task<FetchResult> FetchCoreAsync(Uri url, Stream destination, Func<long, Task> progress, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = ConnectAsync,
        };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        var current = url;
        for (var hops = 0; ; hops++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response;
            try
            {
                using var headerCts = IdleSource(token);
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
            }
            catch (HttpRequestException e) when (e.InnerException is MediaFetchException inner) { throw inner; }
            catch (HttpRequestException e) { throw new MediaFetchException($"the download failed: {e.Message}"); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new MediaFetchException("the server did not respond in time"); }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (hops >= _options.MaxRedirects) throw new MediaFetchException($"too many redirects (more than {_options.MaxRedirects})");
                    var location = response.Headers.Location ?? throw new MediaFetchException("redirect without a Location header");
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (UrlFetchPolicy.RefuseRedirect(current, next) is { } reason) throw new MediaFetchException($"refused: {reason}");
                    current = next;
                    continue;
                }
                if (status != 200) throw new MediaFetchException($"the server answered {status}");

                var max = _options.MaxBytes;
                if (response.Content.Headers.ContentLength is { } len && len > max)
                    throw new MediaFetchException($"larger than the {FormatLimit(max)} limit");
                return await CopyAsync(response, destination, progress, token, max);
            }
        }
    }

    private async Task<FetchResult> CopyAsync(HttpResponseMessage response, Stream destination, Func<long, Task> progress, CancellationToken token, long max)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        await using var body = await response.Content.ReadAsStreamAsync(token);
        while (true)
        {
            int read;
            try
            {
                using var idle = IdleSource(token);
                read = await body.ReadAsync(buffer, idle.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new MediaFetchException("the download stalled"); }
            catch (HttpRequestException e) { throw new MediaFetchException($"the download failed: {e.Message}"); }
            catch (IOException e) { throw new MediaFetchException($"the download failed: {e.Message}"); }
            if (read == 0) break;
            total += read;
            if (total > max) throw new MediaFetchException($"larger than the {FormatLimit(max)} limit");
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
            await progress(total);
        }
        return new FetchResult(total, Convert.ToHexStringLower(hash.GetHashAndReset()), SuggestedName(response));
    }

    /// <summary>A token that fires on cancellation of <paramref name="token"/> or after the idle timeout.</summary>
    private IdleToken IdleSource(CancellationToken token) => new(new CancellationTokenSource(_options.FetchIdleTimeout, _time), token);

    private sealed class IdleToken : IDisposable
    {
        private readonly CancellationTokenSource _idle;
        private readonly CancellationTokenSource _linked;
        public IdleToken(CancellationTokenSource idle, CancellationToken token)
        {
            _idle = idle;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(token, idle.Token);
        }
        public CancellationToken Token => _linked.Token;
        public void Dispose() { _linked.Dispose(); _idle.Dispose(); }
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        IPAddress[] addresses;
        try { addresses = await _resolve(host, ct); }
        catch (SocketException) { throw new MediaFetchException($"could not resolve {host}"); }
        if (addresses.Length == 0) throw new MediaFetchException($"could not resolve {host}");
        if (addresses.Any(_isRefused))
        {
            _log.LogWarning("Refused fetch from {Host}: resolves to a private, loopback or link-local address", host);
            throw new MediaFetchException($"refused: {host} resolves to a private, loopback or link-local address");
        }

        SocketException? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e) { socket.Dispose(); last = e; }
            catch { socket.Dispose(); throw; }
        }
        throw new MediaFetchException($"could not connect to {host}: {last?.Message}");
    }

    private static string FormatLimit(long bytes) => bytes % (1L << 30) == 0 ? $"{bytes >> 30} GiB" : $"{bytes} byte";

    private static string? SuggestedName(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var name = disposition?.FileNameStar ?? disposition?.FileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            var segment = response.RequestMessage?.RequestUri?.Segments.LastOrDefault();
            name = segment is null ? null : Uri.UnescapeDataString(segment.TrimEnd('/'));
        }
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = Path.GetFileName(name.Trim().Trim('"').Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? null : name;
    }

    private static IReadOnlySet<IPAddress> HostAddresses()
    {
        var set = new HashSet<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var ua in nic.GetIPProperties().UnicastAddresses) set.Add(ua.Address);
        }
        catch (NetworkInformationException) { }
        return set;
    }
}

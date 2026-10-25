using System.Net;
using System.Net.Sockets;

namespace Maldex.Core.Media;

/// <summary>Rules for fetching media from a URL: what may be requested and which addresses may be connected to.</summary>
public static class UrlFetchPolicy
{
    private const int MaxUrlLength = 2048;

    public static string? RefuseUrl(Uri url)
    {
        if (!url.IsAbsoluteUri) return "the URL must be absolute";
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) return "only http and https URLs are supported";
        if (url.UserInfo.Length > 0) return "credentials in URLs are not supported";
        if (url.OriginalString.Length > MaxUrlLength) return $"the URL is longer than {MaxUrlLength} characters";
        return null;
    }

    public static string? RefuseRedirect(Uri from, Uri to)
    {
        if (RefuseUrl(to) is { } reason) return reason;
        if (from.Scheme == Uri.UriSchemeHttps && to.Scheme == Uri.UriSchemeHttp) return "a redirect from https to http is not allowed";
        return null;
    }

    public static bool IsRefused(IPAddress ip, bool allowPrivate, IReadOnlySet<IPAddress> hostAddresses)
    {
        ip = Unwrap(ip);
        if (AlwaysRefused(ip)) return true;
        if (allowPrivate) return false;
        return IsPrivate(ip) || hostAddresses.Any(h => Unwrap(h).Equals(ip));
    }

    /// <summary>IPv4-mapped, 6to4 and NAT64 addresses are judged by the IPv4 address they carry.</summary>
    private static IPAddress Unwrap(IPAddress ip)
    {
        for (var i = 0; i < 3 && ip.AddressFamily == AddressFamily.InterNetworkV6; i++)
        {
            var b = ip.GetAddressBytes();
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            else if (b[..12].All(x => x == 0) && (b[12] != 0 || b[13] != 0 || b[14] != 0)) ip = new IPAddress(b[12..16]); // IPv4-compatible ::a.b.c.d
            else if (b[0] == 0x20 && b[1] == 0x02) ip = new IPAddress(b[2..6]);
            else if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(x => x == 0)) ip = new IPAddress(b[12..16]);
            else break;
        }
        return ip;
    }

    private static bool AlwaysRefused(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 0 || b[0] >= 224; // 0.0.0.0/8, multicast 224/4, 240/4 and broadcast
        return ip.Equals(IPAddress.IPv6None) || b[0] == 0xff; // :: and ff00::/8
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 127
                || b[0] == 10
                || (b[0] == 172 && (b[1] & 0xf0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && (b[1] & 0xc0) == 64);
        return IPAddress.IsLoopback(ip)
            || (b[0] == 0xfe && (b[1] & 0xc0) == 0x80)  // link-local fe80::/10
            || (b[0] == 0xfe && (b[1] & 0xc0) == 0xc0)  // site-local fec0::/10
            || (b[0] & 0xfe) == 0xfc                    // ULA fc00::/7
            || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0)  // Teredo 2001:0::/32
            || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0 && b[5] == 0x01); // NAT64 local use 64:ff9b:1::/48
    }
}

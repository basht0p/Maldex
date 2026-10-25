using System.Net;
using Maldex.Core.Media;
using Xunit;

namespace Maldex.Core.Tests.Media;

public class UrlFetchPolicyTests
{
    private static readonly IReadOnlySet<IPAddress> NoHost = new HashSet<IPAddress>();

    [Theory]
    [InlineData("http://example.com/a.iso")]
    [InlineData("https://example.com/a.iso")]
    public void Http_and_https_are_allowed(string url) => Assert.Null(UrlFetchPolicy.RefuseUrl(new Uri(url)));

    [Theory]
    [InlineData("ftp://example.com/a.iso")]
    [InlineData("file:///etc/passwd")]
    public void Other_schemes_are_refused(string url) => Assert.NotNull(UrlFetchPolicy.RefuseUrl(new Uri(url)));

    [Fact]
    public void Relative_urls_are_refused() =>
        Assert.NotNull(UrlFetchPolicy.RefuseUrl(new Uri("/a.iso", UriKind.Relative)));

    [Fact]
    public void Credentials_in_urls_are_refused() =>
        Assert.Equal("credentials in URLs are not supported", UrlFetchPolicy.RefuseUrl(new Uri("http://user:pw@host/")));

    [Fact]
    public void Urls_over_2048_characters_are_refused()
    {
        Assert.NotNull(UrlFetchPolicy.RefuseUrl(new Uri("https://example.com/" + new string('a', 2040))));
        Assert.Null(UrlFetchPolicy.RefuseUrl(new Uri("https://example.com/" + new string('a', 2000))));
    }

    [Fact]
    public void Https_to_http_redirect_is_refused() =>
        Assert.NotNull(UrlFetchPolicy.RefuseRedirect(new Uri("https://a.example/x"), new Uri("http://b.example/x")));

    [Fact]
    public void Other_redirects_are_allowed()
    {
        Assert.Null(UrlFetchPolicy.RefuseRedirect(new Uri("http://a.example/x"), new Uri("https://b.example/x")));
        Assert.Null(UrlFetchPolicy.RefuseRedirect(new Uri("https://a.example/x"), new Uri("https://b.example/x")));
        Assert.Null(UrlFetchPolicy.RefuseRedirect(new Uri("http://a.example/x"), new Uri("http://b.example/x")));
    }

    [Fact]
    public void Redirects_are_checked_against_the_url_rules()
    {
        Assert.NotNull(UrlFetchPolicy.RefuseRedirect(new Uri("https://a.example/x"), new Uri("ftp://b.example/x")));
        Assert.NotNull(UrlFetchPolicy.RefuseRedirect(new Uri("https://a.example/x"), new Uri("https://u:p@b.example/x")));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.31.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("2002:c0a8:0101::1")]
    [InlineData("64:ff9b::c0a8:101")]
    [InlineData("::ffff:127.0.0.1")]
    public void Private_addresses_are_refused(string ip) =>
        Assert.True(UrlFetchPolicy.IsRefused(IPAddress.Parse(ip), false, NoHost));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700::1111")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2002:0808:0808::1")]
    [InlineData("64:ff9b::808:808")]
    public void Public_addresses_are_allowed(string ip) =>
        Assert.False(UrlFetchPolicy.IsRefused(IPAddress.Parse(ip), false, NoHost));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.31.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("2002:c0a8:0101::1")]
    public void Allow_private_lets_private_addresses_pass(string ip) =>
        Assert.False(UrlFetchPolicy.IsRefused(IPAddress.Parse(ip), true, NoHost));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("::")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("ff02::1")]
    [InlineData("255.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("::ffff:0.0.0.0")]
    [InlineData("::ffff:224.0.0.1")]
    [InlineData("2002:e000:0001::1")]
    public void Unspecified_multicast_broadcast_and_reserved_stay_refused_even_when_private_is_allowed(string ip) =>
        Assert.True(UrlFetchPolicy.IsRefused(IPAddress.Parse(ip), true, NoHost));

    [Fact]
    public void The_hosts_own_addresses_are_refused_unless_private_is_allowed()
    {
        var own = IPAddress.Parse("203.0.113.7");
        var host = new HashSet<IPAddress> { own };
        Assert.True(UrlFetchPolicy.IsRefused(own, false, host));
        Assert.False(UrlFetchPolicy.IsRefused(own, true, host));
        Assert.False(UrlFetchPolicy.IsRefused(IPAddress.Parse("203.0.113.8"), false, host));
    }

    [Fact]
    public void The_hosts_own_address_is_matched_through_ipv4_mapping()
    {
        var host = new HashSet<IPAddress> { IPAddress.Parse("203.0.113.7") };
        Assert.True(UrlFetchPolicy.IsRefused(IPAddress.Parse("::ffff:203.0.113.7"), false, host));
    }
}

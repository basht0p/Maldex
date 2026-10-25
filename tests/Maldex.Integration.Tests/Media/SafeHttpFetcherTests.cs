using Microsoft.Extensions.Logging;
using Xunit;
using System.Net;
using System.Security.Cryptography;
using Maldex.Infrastructure.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Maldex.Integration.Tests.Media;

public sealed class SafeHttpFetcherTests : IAsyncLifetime
{
    private static readonly IPAddress Public = IPAddress.Parse("127.0.0.2");
    private static readonly IPAddress Private = IPAddress.Loopback;

    private readonly List<WebApplication> _servers = [];
    private readonly byte[] _payload = RandomNumberGenerator.GetBytes(300_000);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _servers) await s.DisposeAsync();
    }

    private async Task<int> Serve(IPAddress ip, Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(ip, 0));
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        _servers.Add(app);
        return new Uri(app.Urls.First()).Port;
    }

    private static SafeHttpFetcher Fetcher(MediaOptions? options = null, bool seams = true)
    {
        options ??= new MediaOptions();
        return new SafeHttpFetcher(Options.Create(options), TimeProvider.System, NullLogger<SafeHttpFetcher>.Instance,
            seams ? Resolve : null,
            seams ? (ip => ip.Equals(Private)) : null);
    }

    private static Task<IPAddress[]> Resolve(string host, CancellationToken ct) => host switch
    {
        "public.test" => Task.FromResult(new[] { Public }),
        "rebind.test" => Task.FromResult(new[] { Public, Private }),
        "localhost" => Task.FromResult(new[] { Private }),
        _ => Dns.GetHostAddressesAsync(host, ct),
    };

    private static async Task<(FetchResult? Result, MediaFetchException? Error, long Written)> Run(SafeHttpFetcher f, Uri url)
    {
        var sink = new MemoryStream();
        try { return (await f.FetchAsync(url, sink, _ => Task.CompletedTask, CancellationToken.None), null, sink.Length); }
        catch (MediaFetchException e) { return (null, e, sink.Length); }
    }

    [Fact]
    public async Task Fetch_HashesTheBody_AndReportsProgress()
    {
        var port = await Serve(Public, app => app.MapGet("/files/win11.iso", async (HttpContext c) =>
        {
            c.Response.ContentType = "application/octet-stream";
            await c.Response.Body.WriteAsync(_payload);
        }));
        var sink = new MemoryStream();
        long lastProgress = 0;
        var result = await Fetcher().FetchAsync(new Uri($"http://public.test:{port}/files/win11.iso"), sink,
            n => { lastProgress = n; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(_payload.Length, result.Bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(_payload)), result.Sha256);
        Assert.Equal("win11.iso", result.SuggestedFileName);
        Assert.Equal(_payload, sink.ToArray());
        Assert.Equal(_payload.Length, lastProgress);
    }

    [Fact]
    public async Task Fetch_TakesTheFileNameFromContentDisposition()
    {
        var port = await Serve(Public, app => app.MapGet("/d", (HttpContext c) =>
        {
            c.Response.Headers.ContentDisposition = "attachment; filename=\"../evil/Setup.iso\"";
            return Results.Bytes(_payload);
        }));
        var (result, error, _) = await Run(Fetcher(), new Uri($"http://public.test:{port}/d"));
        Assert.Null(error);
        Assert.Equal("Setup.iso", result!.SuggestedFileName);
    }

    [Fact]
    public async Task Redirect_ToPrivateAddress_IsRefused()
    {
        var privatePort = await Serve(Private, app => app.MapGet("/secret", () => "lan"));
        var port = await Serve(Public, app => app.MapGet("/", (HttpContext c) =>
        {
            c.Response.Redirect($"http://127.0.0.1:{privatePort}/secret");
        }));
        var (_, error, written) = await Run(Fetcher(), new Uri($"http://public.test:{port}/"));
        Assert.NotNull(error);
        Assert.Contains("refused", error!.Message);
        Assert.Equal(0, written);
    }

    [Fact]
    public async Task Redirect_ToLocalhostName_IsRefused()
    {
        var privatePort = await Serve(Private, app => app.MapGet("/secret", () => "lan"));
        var port = await Serve(Public, app => app.MapGet("/", (HttpContext c) =>
        {
            c.Response.Redirect($"http://localhost:{privatePort}/secret");
        }));
        var (_, error, written) = await Run(Fetcher(), new Uri($"http://public.test:{port}/"));
        Assert.NotNull(error);
        Assert.Contains("refused: localhost resolves to a private, loopback or link-local address", error!.Message);
        Assert.Equal(0, written);
    }

    [Fact]
    public async Task MixedResolution_IsRefused()
    {
        var port = await Serve(Public, app => app.MapGet("/", () => "x"));
        var (_, error, _) = await Run(Fetcher(), new Uri($"http://rebind.test:{port}/"));
        Assert.NotNull(error);
        Assert.Contains("refused: rebind.test resolves to a private, loopback or link-local address", error!.Message);
    }

    [Fact]
    public async Task Redirect_Relative_IsFollowed()
    {
        var port = await Serve(Public, app =>
        {
            app.MapGet("/a", (HttpContext c) => { c.Response.StatusCode = 302; c.Response.Headers.Location = "/b/final.iso"; });
            app.MapGet("/b/final.iso", () => Results.Bytes(_payload));
        });
        var (result, error, _) = await Run(Fetcher(), new Uri($"http://public.test:{port}/a"));
        Assert.Null(error);
        Assert.Equal(_payload.Length, result!.Bytes);
        Assert.Equal("final.iso", result.SuggestedFileName);
    }

    [Fact]
    public async Task TooManyRedirects_Fails()
    {
        var port = await Serve(Public, app => app.MapGet("/loop", (HttpContext c) => { c.Response.Redirect("/loop"); }));
        var (_, error, _) = await Run(Fetcher(new MediaOptions { MaxRedirects = 3 }), new Uri($"http://public.test:{port}/loop"));
        Assert.NotNull(error);
        Assert.Contains("too many redirects", error!.Message);
    }

    [Fact]
    public async Task ContentLengthOverCap_FailsWithoutReadingBody()
    {
        var bodyStarted = false;
        var port = await Serve(Public, app => app.MapGet("/", async (HttpContext c) =>
        {
            c.Response.ContentLength = 1_000_000;
            await c.Response.StartAsync();
            bodyStarted = true;
            await c.Response.Body.WriteAsync(new byte[1000]);
        }));
        var (_, error, written) = await Run(Fetcher(new MediaOptions { MaxBytes = 1000 }), new Uri($"http://public.test:{port}/"));
        Assert.NotNull(error);
        Assert.Contains("larger than the", error!.Message);
        Assert.Equal(0, written);
        _ = bodyStarted;
    }

    [Fact]
    public async Task StreamOverCap_Fails()
    {
        var port = await Serve(Public, app => app.MapGet("/", async (HttpContext c) =>
        {
            // no Content-Length: chunked
            await c.Response.Body.WriteAsync(new byte[5000]);
        }));
        var (_, error, _) = await Run(Fetcher(new MediaOptions { MaxBytes = 4096 }), new Uri($"http://public.test:{port}/"));
        Assert.NotNull(error);
        Assert.Equal("larger than the 4096 byte limit", error!.Message);
    }

    [Fact]
    public async Task StreamExactlyAtCap_Succeeds()
    {
        var port = await Serve(Public, app => app.MapGet("/", async (HttpContext c) => await c.Response.Body.WriteAsync(new byte[4096])));
        var (result, error, _) = await Run(Fetcher(new MediaOptions { MaxBytes = 4096 }), new Uri($"http://public.test:{port}/"));
        Assert.Null(error);
        Assert.Equal(4096, result!.Bytes);
    }

    [Fact]
    public async Task Non200_Fails()
    {
        var port = await Serve(Public, app => app.MapGet("/", (HttpContext c) => { c.Response.StatusCode = 404; }));
        var (_, error, _) = await Run(Fetcher(), new Uri($"http://public.test:{port}/"));
        Assert.NotNull(error);
        Assert.Contains("404", error!.Message);
    }

    [Fact]
    public async Task DefaultClassifier_RefusesLoopback_WhenPrivateFetchIsNotAllowed()
    {
        var port = await Serve(Private, app => app.MapGet("/", () => Results.Bytes(_payload)));
        var (_, error, written) = await Run(Fetcher(new MediaOptions { AllowPrivateFetch = false }, seams: false), new Uri($"http://127.0.0.1:{port}/"));
        Assert.NotNull(error);
        Assert.Contains("refused: 127.0.0.1 resolves to a private, loopback or link-local address", error!.Message);
        Assert.Equal(0, written);
    }

    [Fact]
    public async Task DefaultClassifier_AllowsLoopback_WhenPrivateFetchIsAllowed()
    {
        var port = await Serve(Private, app => app.MapGet("/", () => Results.Bytes(_payload)));
        var (result, error, _) = await Run(Fetcher(new MediaOptions { AllowPrivateFetch = true }, seams: false), new Uri($"http://127.0.0.1:{port}/"));
        Assert.Null(error);
        Assert.Equal(_payload.Length, result!.Bytes);
    }

    [Fact]
    public async Task NonHttpUrl_IsRefused()
    {
        var (_, error, _) = await Run(Fetcher(), new Uri("ftp://public.test/x.iso"));
        Assert.NotNull(error);
        Assert.Contains("only http and https", error!.Message);
    }
}

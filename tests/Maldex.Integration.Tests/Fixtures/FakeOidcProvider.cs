using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Maldex.Integration.Tests.Fixtures;

/// <summary>
/// A minimal OpenID Connect provider on a real Kestrel listener (<c>127.0.0.1:0</c>) inside the test process: discovery, JWKS,
/// <c>/authorize</c> (no login page: a 302 straight back to <c>redirect_uri</c> with a code for the identity set by
/// <see cref="NextUser"/>) and <c>/token</c> (checks the PKCE <c>code_verifier</c>, the client and, when required, the client
/// secret; answers with an RS256 <c>id_token</c>).
/// </summary>
public sealed class FakeOidcProvider : IAsyncDisposable
{
    private sealed record Identity(string Sub, string? Name, string? Email, IDictionary<string, string[]>? Extra, bool SingleAsString);
    private sealed record Grant(Identity User, string ClientId, string RedirectUri, string? Nonce, string? Challenge, string? ChallengeMethod);

    private readonly WebApplication _app;
    private readonly RSA _key = RSA.Create(2048);
    private readonly string _kid = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, Grant> _codes = new();
    private volatile Identity _next = new("user-1", "User One", null, null, false);
    private volatile string? _clientSecret;

    /// <summary>The issuer and discovery base, <c>http://127.0.0.1:&lt;port&gt;</c> (no trailing slash).</summary>
    public string Authority { get; }

    /// <summary>The form fields of the last token request (to check what the client sent).</summary>
    public IReadOnlyDictionary<string, string>? LastTokenRequest { get; private set; }

    private FakeOidcProvider(WebApplication app, string authority)
    {
        _app = app;
        Authority = authority;
    }

    public static async Task<FakeOidcProvider> StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        // The pipeline is built at start, before the port (and so the issuer) is known.
        FakeOidcProvider? fake = null;
        app.Run(ctx => fake is null ? Status(ctx, 503) : fake.HandleAsync(ctx));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake = new FakeOidcProvider(app, address.TrimEnd('/'));
        return fake;
    }

    /// <summary>
    /// The identity every following authorize call returns (until changed). Extra claims are JSON arrays, or with
    /// <paramref name="singleValuesAsStrings"/> a one-value claim is a plain JSON string (as some IdPs send one group).
    /// </summary>
    public void NextUser(string sub, string? name = null, string? email = null, IDictionary<string, string[]>? extraClaims = null,
        bool singleValuesAsStrings = false) =>
        _next = new Identity(sub, name, email, extraClaims, singleValuesAsStrings);

    /// <summary>Null: a public client (no secret checked). Otherwise the token request must carry exactly this secret.</summary>
    public void RequireClientSecret(string? secret) => _clientSecret = secret;

    private Task HandleAsync(HttpContext ctx) => ctx.Request.Path.Value switch
    {
        "/.well-known/openid-configuration" => JsonAsync(ctx, 200, new Dictionary<string, object>
        {
            ["issuer"] = Authority,
            ["authorization_endpoint"] = Authority + "/authorize",
            ["token_endpoint"] = Authority + "/token",
            ["jwks_uri"] = Authority + "/jwks",
            ["response_types_supported"] = new[] { "code" },
            ["response_modes_supported"] = new[] { "query" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_post", "client_secret_basic", "none" },
        }),
        "/jwks" => Jwks(ctx),
        "/authorize" => Authorize(ctx),
        "/token" => TokenAsync(ctx),
        _ => Status(ctx, 404),
    };

    private Task Jwks(HttpContext ctx)
    {
        var p = _key.ExportParameters(false);
        return JsonAsync(ctx, 200, new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = _kid, n = B64(p.Modulus!), e = B64(p.Exponent!) } } });
    }

    private Task Authorize(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        string? Get(string k) => q.TryGetValue(k, out var v) ? v.ToString() : null;
        var redirectUri = Get("redirect_uri");
        if (Get("response_type") != "code" || string.IsNullOrEmpty(redirectUri) || string.IsNullOrEmpty(Get("client_id")))
            return Status(ctx, 400);

        var code = B64(RandomNumberGenerator.GetBytes(24));
        _codes[code] = new Grant(_next, Get("client_id")!, redirectUri, Get("nonce"), Get("code_challenge"), Get("code_challenge_method"));
        var target = QueryHelpers.AddQueryString(redirectUri, new Dictionary<string, string?> { ["code"] = code, ["state"] = Get("state") });
        ctx.Response.Redirect(target);
        return Task.CompletedTask;
    }

    private async Task TokenAsync(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method) || !ctx.Request.HasFormContentType) { await Status(ctx, 400); return; }
        var form = await ctx.Request.ReadFormAsync();
        var fields = form.ToDictionary(f => f.Key, f => f.Value.ToString());
        LastTokenRequest = fields;
        string? Get(string k) => fields.TryGetValue(k, out var v) ? v : null;

        // client_secret_basic is accepted too.
        var clientId = Get("client_id");
        var secret = Get("client_secret");
        if (ctx.Request.Headers.Authorization.ToString() is { } authz && authz.StartsWith("Basic ", StringComparison.Ordinal))
        {
            var pair = Encoding.UTF8.GetString(Convert.FromBase64String(authz[6..])).Split(':', 2);
            clientId = Uri.UnescapeDataString(pair[0]);
            secret = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : null;
        }

        if (Get("grant_type") != "authorization_code" || Get("code") is not { } code || !_codes.TryRemove(code, out var grant))
        {
            await JsonAsync(ctx, 400, new { error = "invalid_grant" });
            return;
        }
        if (clientId != grant.ClientId || Get("redirect_uri") != grant.RedirectUri)
        {
            await JsonAsync(ctx, 400, new { error = "invalid_grant" });
            return;
        }
        if (_clientSecret is { } required && secret != required)
        {
            await JsonAsync(ctx, 401, new { error = "invalid_client" });
            return;
        }
        // PKCE: the authorize request must have carried an S256 challenge, and the verifier must match it.
        var verifier = Get("code_verifier");
        if (grant.ChallengeMethod != "S256" || string.IsNullOrEmpty(grant.Challenge) || string.IsNullOrEmpty(verifier)
            || B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != grant.Challenge)
        {
            await JsonAsync(ctx, 400, new { error = "invalid_grant", error_description = "pkce" });
            return;
        }

        var accessToken = B64(RandomNumberGenerator.GetBytes(24));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = Authority,
            ["sub"] = grant.User.Sub,
            ["aud"] = grant.ClientId,
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + 300,
            ["at_hash"] = B64(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken))[..16]),
        };
        if (grant.Nonce is not null) claims["nonce"] = grant.Nonce;
        if (grant.User.Name is not null) claims["name"] = grant.User.Name;
        if (grant.User.Email is not null) claims["email"] = grant.User.Email;
        if (grant.User.Extra is not null)
            foreach (var (k, v) in grant.User.Extra) claims[k] = grant.User.SingleAsString && v.Length == 1 ? v[0] : v;

        await JsonAsync(ctx, 200, new { access_token = accessToken, token_type = "Bearer", expires_in = 300, id_token = Sign(claims) });
    }

    private string Sign(Dictionary<string, object> claims)
    {
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT", kid = _kid }));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = _key.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + B64(signature);
    }

    private static string B64(byte[] bytes) => WebEncoders.Base64UrlEncode(bytes);

    private static Task Status(HttpContext ctx, int status)
    {
        ctx.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    private static async Task JsonAsync(HttpContext ctx, int status, object body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(body));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _key.Dispose();
    }
}

using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Infrastructure.Guacamole;
using Maldex.Web.Audit;
using Maldex.Web.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Maldex.Web.Endpoints;

/// <summary>
/// The interactive live view. <c>GET /api/tasks/{id}/tunnel-token</c> hands the browser a single-use token;
/// <c>GET /api/tasks/{id}/tunnel</c> (WebSocket, subprotocol <c>guacamole</c>) consumes it and proxies Guacamole
/// instructions to a local guacd. The VNC password and guacd address stay on the server: the server performs the
/// guacd handshake itself and the browser only ever receives display instructions.
/// </summary>
public static class GuacamoleTunnelEndpoint
{
    public const string Subprotocol = "guacamole";
    private const int DefaultWidth = 1280, DefaultHeight = 800, Dpi = 96;
    /// <summary>The only instructions a browser may send to guacd once connected: input, frame acknowledgements and size/disconnect.</summary>
    private static readonly HashSet<string> AllowedUpstreamOpcodes = new(StringComparer.Ordinal) { "mouse", "key", "sync", "size", "disconnect", "nop", "ack" };

    private sealed class PolicyViolationException(string opcode) : Exception($"Instruction '{opcode}' is not allowed from the browser.");

    private const int MaxClientMessageBytes = 1_048_576;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    public static IEndpointRouteBuilder MapGuacamoleTunnelEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tasks").RequireAuthorization(AuthSetup.AnalystPolicy);

        g.MapGet("{id:guid}/tunnel-token", async (Guid id, MaldexDbContext db, CurrentUser user, GuacamoleTokenService tokens, AuditWriter audit, CancellationToken ct) =>
        {
            var status = await db.Tasks.AsNoTracking().Where(t => t.Id == id).Select(t => (RunStatus?)t.Status).FirstOrDefaultAsync(ct);
            if (status is null) return Results.NotFound();
            if (status != RunStatus.Running)
                return Results.Problem(title: "The live view is only available while the task is running.", statusCode: StatusCodes.Status409Conflict);

            var token = tokens.Issue(id, user.UserId);
            await audit.WriteAsync("session.token", id.ToString(), null, ct); // never log the token itself
            return Results.Ok(new { token });
        });

        // A browser cannot send an Authorization header on a WebSocket upgrade, so the single-use token (issued above to an
        // authenticated Analyst, 60 s, bound to the task) is this route's credential. Same-origin is enforced separately.
        app.MapGet("/api/tasks/{id:guid}/tunnel", TunnelAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> TunnelAsync(
        Guid id, string? token, int? width, int? height, HttpContext ctx, GuacamoleTokenService tokens,
        IVmConsoleLocator locator, IOptions<GuacamoleOptions> options, IServiceScopeFactory scopes, MaldexDbContext db, TimeProvider time,
        ILogger<GuacamoleTunnelLog> log)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) return Results.Problem(title: "A WebSocket upgrade is required.", statusCode: StatusCodes.Status400BadRequest);
        if (!IsSameOrigin(ctx.Request)) return Results.Problem(title: "Cross-origin tunnels are not allowed.", statusCode: StatusCodes.Status403Forbidden);

        var requested = ctx.WebSockets.WebSocketRequestedProtocols.Contains(Subprotocol);
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { SubProtocol = requested ? Subprotocol : null });
        var ct = ctx.RequestAborted;

        try
        {
            if (!tokens.TryConsume(token, out var taskId, out var userId))
            {
                await AuditAsync(db, time, null, "session.join.denied", id, "invalid, reused or expired token", ct);
                await CloseAsync(ws, WebSocketCloseStatus.PolicyViolation, "Invalid tunnel token.");
                return Results.Empty;
            }
            if (taskId != id)
            {
                await AuditAsync(db, time, userId, "session.join.denied", id, "token was issued for another task", ct);
                await CloseAsync(ws, WebSocketCloseStatus.PolicyViolation, "Invalid tunnel token.");
                return Results.Empty;
            }
            if (!await IsRunningAsync(scopes, id, ct))
            {
                await AuditAsync(db, time, userId, "session.join.denied", id, "task is not running", ct);
                await CloseAsync(ws, WebSocketCloseStatus.PolicyViolation, "The task is not running.");
                return Results.Empty;
            }

            var endpoint = await locator.GetAsync(VmHandle.For(id), ct);
            if (endpoint is null)
            {
                await CloseAsync(ws, WebSocketCloseStatus.InternalServerError, "The VM console is not available.");
                return Results.Empty;
            }

            using var tcp = new TcpClient { NoDelay = true };
            GuacamoleInstructionReader reader;
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(ConnectTimeout);
                    await tcp.ConnectAsync(options.Value.GuacdHost, options.Value.GuacdPort, connectCts.Token);
                }
                var stream = tcp.GetStream();
                reader = new GuacamoleInstructionReader(new StreamReader(stream, new UTF8Encoding(false), false, 8192, leaveOpen: true));
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeCts.CancelAfter(HandshakeTimeout);
                var ready = await HandshakeAsync(stream, reader, endpoint, Clamp(width, DefaultWidth), Clamp(height, DefaultHeight), handshakeCts.Token);

                await AuditAsync(db, time, userId, "session.join", id, null, ct);
                await RunAsync(ws, stream, reader, ready, id, options.Value, scopes, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Details (which can name the VNC host) stay in the server log; the browser gets a generic reason.
                log.LogWarning(ex, "Live-view tunnel for task {TaskId} failed: {Error}", id, ex.GetType().Name);
                await CloseAsync(ws, WebSocketCloseStatus.InternalServerError, "The live view could not be established.");
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            // The browser went away.
        }
        return Results.Empty;
    }

    /// <summary>Audit row written for the user named by the token (the anonymous route has no current user). Never put a token in <paramref name="details"/>.</summary>
    private static async Task AuditAsync(MaldexDbContext db, TimeProvider time, Guid? userId, string action, Guid taskId, string? details, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog { At = time.GetUtcNow(), UserId = userId, Action = action, Target = taskId.ToString(), Details = details });
        await db.SaveChangesAsync(ct);
    }

    private static int Clamp(int? v, int fallback) => v is { } x ? Math.Clamp(x, 320, 4096) : fallback;

    /// <summary>Server side of the Guacamole handshake. <paramref name="ready"/> is guacd's ready instruction (forwarded to the browser).</summary>
    private static async Task<string[]> HandshakeAsync(Stream guacd, GuacamoleInstructionReader reader, VncEndpoint vnc, int width, int height, CancellationToken ct)
    {
        await WriteAsync(guacd, GuacamoleProtocol.Encode("select", "vnc"), ct);
        var args = await reader.ReadAsync(ct);
        if (args is not ["args", ..]) throw new InvalidDataException("guacd did not answer select with args.");

        await WriteAsync(guacd, GuacamoleProtocol.Encode("size", width.ToString(), height.ToString(), Dpi.ToString()), ct);
        await WriteAsync(guacd, GuacamoleProtocol.Encode("audio"), ct);
        await WriteAsync(guacd, GuacamoleProtocol.Encode("video"), ct);
        await WriteAsync(guacd, GuacamoleProtocol.Encode("image", "image/png", "image/jpeg"), ct);

        var connect = new string[args.Length];
        connect[0] = "connect";
        for (var i = 1; i < args.Length; i++)
        {
            connect[i] = args[i] switch
            {
                "hostname" => vnc.Host,
                "port" => vnc.Port.ToString(),
                "password" => vnc.Password,
                var name when name.StartsWith("VERSION_", StringComparison.Ordinal) => name, // protocol version negotiation
                _ => "",
            };
        }
        await WriteAsync(guacd, GuacamoleProtocol.Encode(connect), ct);

        var ready = await reader.ReadAsync(ct) ?? throw new InvalidDataException("guacd closed during the handshake.");
        if (ready[0] != "ready") throw new InvalidDataException("guacd refused the connection.");
        return ready;
    }

    private static async Task RunAsync(
        WebSocket ws, Stream guacd, GuacamoleInstructionReader reader, string[] ready, Guid taskId, GuacamoleOptions options,
        IServiceScopeFactory scopes, CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var ct = cts.Token;
        using var sendGate = new SemaphoreSlim(1, 1);

        async Task SendAsync(string text)
        {
            await sendGate.WaitAsync(ct);
            try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct); }
            finally { sendGate.Release(); }
        }

        // The tunnel UUID is the first (internal) instruction a guacamole-common-js tunnel expects.
        await SendAsync(GuacamoleProtocol.Encode(GuacamoleProtocol.InternalOpcode, Guid.NewGuid().ToString()));
        await SendAsync(GuacamoleProtocol.Encode(ready));

        var down = Task.Run(async () =>
        {
            while (await reader.ReadAsync(ct) is { } instruction) await SendAsync(GuacamoleProtocol.Encode(instruction));
        }, ct);

        var up = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                if (r.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Binary tunnel messages are not supported.");
                message.Write(buffer, 0, r.Count);
                if (message.Length > MaxClientMessageBytes) throw new InvalidDataException("Tunnel message too large.");
                if (!r.EndOfMessage) continue;

                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                var forward = new StringBuilder();
                foreach (var instruction in await GuacamoleInstructionReader.ParseAllAsync(text, ct))
                {
                    // Internal instructions (ping) are answered here and never reach guacd.
                    if (instruction[0] == GuacamoleProtocol.InternalOpcode) await SendAsync(GuacamoleProtocol.Encode(instruction));
                    else if (!AllowedUpstreamOpcodes.Contains(instruction[0])) throw new PolicyViolationException(instruction[0]);
                    else forward.Append(GuacamoleProtocol.Encode(instruction));
                }
                if (forward.Length > 0) await WriteAsync(guacd, forward.ToString(), ct);
            }
        }, ct);

        var watch = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(options.StatusPollInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { if (!await IsRunningAsync(scopes, taskId, ct)) return; }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* transient database error: try again next tick */ }
            }
        }, ct);

        var first = await Task.WhenAny(down, up, watch);
        var status = WebSocketCloseStatus.NormalClosure;
        var description = first == watch ? "The task is no longer running." : "The live view ended.";
        if (first == up && up.IsFaulted && up.Exception!.GetBaseException() is PolicyViolationException)
        {
            status = WebSocketCloseStatus.PolicyViolation;
            description = "Instruction not allowed.";
        }
        cts.Cancel();
        try { await Task.WhenAll(down, up, watch); }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or InvalidDataException or FormatException or PolicyViolationException) { }
        await CloseAsync(ws, status, description);
    }

    private static async Task<bool> IsRunningAsync(IServiceScopeFactory scopes, Guid taskId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();
        return await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == taskId && t.Status == RunStatus.Running, ct);
    }

    private static async Task WriteAsync(Stream stream, string text, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await stream.FlushAsync(ct);
    }

    private static async Task CloseAsync(WebSocket ws, WebSocketCloseStatus status, string description)
    {
        if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseOutputAsync(status, description, cts.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
    }

    /// <summary>Browsers always send Origin on a WebSocket upgrade; when present it must be this host. Non-browser clients send none.</summary>
    private static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        // Scheme, host and port must all match; a missing port means the scheme's default (80 / 443).
        var port = request.Host.Port ?? (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        return string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == port;
    }
}

/// <summary>Logger category for the tunnel (static classes cannot be type arguments).</summary>
public sealed class GuacamoleTunnelLog;

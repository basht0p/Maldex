using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Maldex.Infrastructure.Execution;
using Maldex.Infrastructure.Libvirt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Real;

public enum GuardSeverity { Ok, Warn, Fail }

public sealed record GuardResult(string Name, GuardSeverity Severity, string Detail);

/// <summary>Host-level preconditions of real mode. Anything <see cref="GuardSeverity.Fail"/> keeps the orchestrator worker from starting.</summary>
public interface IHostGuardProbe
{
    Task<IReadOnlyList<GuardResult>> CheckAsync(CancellationToken ct);
}

/// <summary>The host's own IPv4 addresses (a guest must not reach any of them).</summary>
public interface IHostAddressProvider
{
    IReadOnlyList<string> GetIPv4Addresses();
}

/// <summary>Every non-loopback, non-link-local IPv4 of every interface except Maldex's own <c>mx*</c> links; docker bridges included.</summary>
public sealed class NetworkInterfaceHostAddressProvider : IHostAddressProvider
{
    public IReadOnlyList<string> GetIPv4Addresses() => Filter(
        NetworkInterface.GetAllNetworkInterfaces().SelectMany(nic => nic.GetIPProperties().UnicastAddresses
            .Select(ua => (nic.Name, nic.NetworkInterfaceType == NetworkInterfaceType.Loopback, ua.Address))));

    /// <summary>The selection rule, separated from the OS so it is testable with fixed input.</summary>
    public static IReadOnlyList<string> Filter(IEnumerable<(string Interface, bool IsLoopbackInterface, IPAddress Address)> candidates)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (name, loopbackIf, a) in candidates)
        {
            if (loopbackIf || name.StartsWith("mx", StringComparison.Ordinal)) continue;
            if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
            var b = a.GetAddressBytes();
            if (b[0] == 0 || (b[0] == 169 && b[1] == 254)) continue;
            result.Add(a.ToString());
        }
        return [.. result];
    }
}

/// <summary>Decides once whether workers that start VMs may run; the result is cached for the process lifetime.</summary>
public interface IWorkerStartGate
{
    Task<bool> AllowStartAsync(CancellationToken ct);
}

public sealed class OpenWorkerStartGate : IWorkerStartGate
{
    public Task<bool> AllowStartAsync(CancellationToken ct) => Task.FromResult(true);
}

public sealed class GuardedWorkerStartGate(IHostGuardProbe probe, ILogger<GuardedWorkerStartGate> logger) : IWorkerStartGate
{
    private readonly object _lock = new();
    private Task<bool>? _decision;

    public Task<bool> AllowStartAsync(CancellationToken ct)
    {
        lock (_lock) return _decision ??= EvaluateAsync();
    }

    private async Task<bool> EvaluateAsync()
    {
        IReadOnlyList<GuardResult> results;
        try { results = await probe.CheckAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogCritical(e, "Real-mode host guards could not be evaluated; the orchestrator worker will NOT start.");
            return false;
        }
        foreach (var r in results.Where(r => r.Severity == GuardSeverity.Warn))
            logger.LogWarning("Real-mode guard '{Guard}': {Detail}", r.Name, r.Detail);
        var failed = results.Where(r => r.Severity == GuardSeverity.Fail).ToList();
        if (failed.Count == 0)
        {
            logger.LogInformation("Real-mode host guards passed ({Count} checks).", results.Count);
            return true;
        }
        foreach (var r in failed) logger.LogCritical("Real-mode guard FAILED '{Guard}': {Detail}", r.Name, r.Detail);
        logger.LogCritical("Real-mode host guards failed; the orchestrator worker will NOT start (the web app stays up), so leftover VMs and network namespaces from earlier runs are NOT being reconciled. Run ./build.sh real-setup, then restart.");
        return false;
    }
}

/// <summary>
/// Starts the wrapped hosted service in the background once <see cref="IWorkerStartGate"/> allows it, so a slow guard check
/// never delays the web host (Kestrel) from coming up. The host only watches <see cref="BackgroundService.ExecuteTask"/> of
/// services it registered itself, so this wrapper does it for the inner one: a failed start or a faulted ExecuteTask is
/// logged as critical and stops the application (a dead orchestrator must not look healthy). A denied gate is not a fault:
/// the web app stays up, as the guard's own critical log explains.
/// </summary>
public sealed class GatedHostedService<T>(T inner, IWorkerStartGate gate, IHostApplicationLifetime lifetime, ILogger logger)
    : IHostedService where T : class, IHostedService
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _start;
    private volatile bool _started;
    private volatile bool _stopping;

    public Task StartAsync(CancellationToken ct)
    {
        _start = Task.Run(async () =>
        {
            if (!await gate.AllowStartAsync(_cts.Token).ConfigureAwait(false)) return;
            try
            {
                await inner.StartAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                Fatal(e, "failed to start");
                return;
            }
            _started = true;
            if (inner is BackgroundService { ExecuteTask: { } execute }) _ = WatchAsync(execute);
        });
        return Task.CompletedTask;
    }

    private async Task WatchAsync(Task execute)
    {
        try { await execute.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stopping || _cts.IsCancellationRequested) { }
        catch (Exception e) { Fatal(e, "stopped with an error"); }
    }

    private void Fatal(Exception e, string what)
    {
        if (_stopping) return;
        logger.LogCritical(e, "Hosted service {Service} {What}; stopping the application.", inner.GetType().Name, what);
        lifetime.StopApplication();
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stopping = true;
        if (_start is not null)
        {
            try { await _start.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { await _cts.CancelAsync().ConfigureAwait(false); }
            catch (Exception) { /* the gate logs its own failures */ }
        }
        if (_started) await inner.StopAsync(ct).ConfigureAwait(false);
    }
}

public static class GatedHostedServiceExtensions
{
    /// <summary>Hosts <typeparamref name="T"/> (already registered as a service) behind <see cref="IWorkerStartGate"/>.</summary>
    public static IServiceCollection AddGatedHostedService<T>(this IServiceCollection services) where T : class, IHostedService =>
        services.AddSingleton<IHostedService>(sp => new GatedHostedService<T>(sp.GetRequiredService<T>(), sp.GetRequiredService<IWorkerStartGate>(),
            sp.GetRequiredService<IHostApplicationLifetime>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<GatedHostedService<T>>()));
}

public sealed class HostGuardProbe(IProcessRunner runner, IOptions<LibvirtOptions> libvirt, IConfiguration configuration) : IHostGuardProbe
{
    public static readonly string[] RequiredUsers = ["maldex-mitm", "maldex-isroot", "maldex-isuser"];
    private static readonly string[] BridgeNfCalls =
    [
        "/proc/sys/net/bridge/bridge-nf-call-iptables",
        "/proc/sys/net/bridge/bridge-nf-call-ip6tables",
        "/proc/sys/net/bridge/bridge-nf-call-arptables",
    ];

    public async Task<IReadOnlyList<GuardResult>> CheckAsync(CancellationToken ct)
    {
        var results = new List<GuardResult>();
        foreach (var family in new[] { "inet", "bridge" })
            results.Add(await RunAsync($"host guard table ({family} maldex_guard)", "sudo", ["-n", "nft", "list", "table", family, "maldex_guard"], ct));

        results.Add(CheckBrNetfilter());

        foreach (var user in RequiredUsers)
            results.Add(await RunAsync($"system user {user}", "id", ["-u", user], ct));

        results.Add(CheckKvm());

        var o = libvirt.Value;
        results.Add(await RunAsync($"virsh -c {o.Uri} version", o.VirshBinary, ["-c", o.Uri, "version"], ct));

        var images = configuration.GetSection("Maldex:Images").Get<string[]>() is { Length: > 0 } configured ? configured : ["win11-26h2"];
        foreach (var image in images)
        {
            var path = Path.Combine(Path.GetFullPath(o.BaseImageDir), image + ".qcow2");
            results.Add(File.Exists(path)
                ? new GuardResult($"base image {image}", GuardSeverity.Ok, path)
                : new GuardResult($"base image {image}", GuardSeverity.Warn, $"{path} does not exist; real runs of this image will fail until it is built (./build.sh build-guest)."));
        }
        return results;
    }

    private static GuardResult CheckBrNetfilter()
    {
        const string name = "br_netfilter does not filter bridged traffic";
        var seen = new List<string>();
        foreach (var file in BridgeNfCalls)
        {
            try
            {
                if (!File.Exists(file)) continue;
                var v = File.ReadAllText(file).Trim();
                if (v == "1")
                    return new GuardResult(name, GuardSeverity.Fail, $"{file} is 1; the host guard assumes bridged frames bypass netfilter (sysctl -w {file["/proc/sys/".Length..].Replace('/', '.')}=0)");
                seen.Add($"{Path.GetFileName(file)}={v}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new GuardResult(name, GuardSeverity.Fail, "cannot read " + file + ": " + e.Message);
            }
        }
        return new GuardResult(name, GuardSeverity.Ok, seen.Count == 0 ? "br_netfilter not loaded" : string.Join(", ", seen));
    }

    private static GuardResult CheckKvm()
    {
        try
        {
            using var _ = new FileStream("/dev/kvm", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return new GuardResult("/dev/kvm usable", GuardSeverity.Ok, "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new GuardResult("/dev/kvm usable", GuardSeverity.Fail, e.Message + " (./build.sh real-setup grants the ACL)");
        }
    }

    private async Task<GuardResult> RunAsync(string name, string file, string[] args, CancellationToken ct)
    {
        try
        {
            var r = await runner.RunAsync(file, args, ct, timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            return r.ExitCode == 0
                ? new GuardResult(name, GuardSeverity.Ok, "")
                : new GuardResult(name, GuardSeverity.Fail, $"exit {r.ExitCode}: {Truncate(r.Stderr)}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new GuardResult(name, GuardSeverity.Fail, e.Message);
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim();
}

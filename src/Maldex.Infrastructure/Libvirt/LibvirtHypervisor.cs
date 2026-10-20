using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Maldex.Core.Abstractions;
using Maldex.Infrastructure.Process;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Libvirt;

internal static partial class LibvirtPaths
{
    [GeneratedRegex("^maldex-([0-9a-f]{32})$")]
    private static partial Regex VmName();

    /// <summary>Relative paths resolve against the process cwd, exactly like <c>AgentChannelOptions.RunDir</c>.</summary>
    public static string RunRoot(LibvirtOptions o) => Path.GetFullPath(o.RunDir);
    public static string BaseRoot(LibvirtOptions o) => Path.GetFullPath(o.BaseImageDir);

    /// <summary>Rejects anything but <c>maldex-{32 hex}</c> so a handle can never escape the run directory.</summary>
    public static string RunDirFor(LibvirtOptions o, VmHandle vm)
    {
        if (TryParse(vm.Name) is null) throw new ArgumentException($"Invalid VM name '{vm.Name}'.", nameof(vm));
        return Path.Combine(RunRoot(o), vm.Name);
    }

    public static VmHandle? TryParse(string name)
    {
        var m = VmName().Match(name);
        return m.Success ? new VmHandle(Guid.ParseExact(m.Groups[1].Value, "N"), name) : null;
    }
}

/// <summary>Session-libvirt (<c>qemu:///session</c>) hypervisor: transient domains over qcow2 overlays.</summary>
public sealed class LibvirtHypervisor(IOptions<LibvirtOptions> options, IProcessRunner runner) : IHypervisor
{
    private const string VncAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private static readonly SemaphoreSlim StartLock = new(1, 1);
    private readonly LibvirtOptions _o = options.Value;

    public async Task<VmHandle> StartAsync(VmSpec spec, CancellationToken ct)
    {
        var handle = VmHandle.For(spec.TaskId);
        var runDir = LibvirtPaths.RunDirFor(_o, handle);
        var basePath = Path.Combine(LibvirtPaths.BaseRoot(_o), spec.Image + ".qcow2");
        var varsPath = Path.Combine(LibvirtPaths.BaseRoot(_o), spec.Image + ".vars.fd");
        if (spec.Image.Contains('/') || spec.Image.Contains('\\') || spec.Image.Contains(".."))
            throw new VmStartException($"invalid image id '{spec.Image}'");
        if (!File.Exists(basePath)) throw new VmStartException($"base image {basePath} not found");
        if (!File.Exists(varsPath)) throw new VmStartException($"UEFI vars template {varsPath} not found");

        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(runDir);
            else Directory.CreateDirectory(runDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var overlay = Path.Combine(runDir, "overlay.qcow2");
            var nvram = Path.Combine(runDir, "nvram.fd");
            await new QemuImageCli(runner, _o.QemuImgBinary).CreateOverlayAsync(basePath, overlay, ct);
            File.Copy(varsPath, nvram, overwrite: true);

            var password = RandomNumberGenerator.GetString(VncAlphabet, 8);
            var passFile = Path.Combine(runDir, "vnc.pass");
            var passOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) passOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var fs = new FileStream(passFile, passOptions))
            await using (var w = new StreamWriter(fs))
                await w.WriteAsync(password.AsMemory(), ct);

            var xmlPath = Path.Combine(runDir, "domain.xml");
            // Serialise port probing through to domain creation so concurrent starts cannot pick the same port.
            await StartLock.WaitAsync(ct);
            try
            {
                var port = FindFreeVncPort();
                var paths = new DomainPaths(overlay, nvram, Path.Combine(runDir, "agent.sock"), port, password);
                await File.WriteAllTextAsync(xmlPath, DomainXmlBuilder.Build(spec, paths, _o), ct);
                var r = await runner.RunAsync(_o.VirshBinary, ["-c", _o.Uri, "create", xmlPath], ct);
                if (r.ExitCode != 0)
                    throw new VmStartException($"virsh create failed ({r.ExitCode}): {r.Stderr.Trim()}");
            }
            finally { StartLock.Release(); }
            return handle;
        }
        catch (Exception ex)
        {
            try { await DestroyAsync(handle, CancellationToken.None); } catch { /* best effort */ }
            if (ex is VmStartException) throw;
            if (ex is OperationCanceledException) throw;
            throw new VmStartException($"failed to start {handle.Name}: {ex.Message}");
        }
    }

    public async Task DestroyAsync(VmHandle handle, CancellationToken ct)
    {
        var runDir = LibvirtPaths.RunDirFor(_o, handle);
        var r = await runner.RunAsync(_o.VirshBinary, ["-c", _o.Uri, "destroy", handle.Name], ct);
        if (r.ExitCode != 0
            && !r.Stderr.Contains("not found", StringComparison.OrdinalIgnoreCase)
            && !r.Stderr.Contains("not running", StringComparison.OrdinalIgnoreCase)
            && !r.Stderr.Contains("not active", StringComparison.OrdinalIgnoreCase)
            && !r.Stderr.Contains("failed to get domain", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"virsh destroy {handle.Name} failed ({r.ExitCode}): {r.Stderr.Trim()}");
        if (Directory.Exists(runDir)) Directory.Delete(runDir, recursive: true);
    }

    public async Task<IReadOnlyList<VmHandle>> ListAsync(CancellationToken ct)
    {
        var r = await runner.RunAsync(_o.VirshBinary, ["-c", _o.Uri, "list", "--all", "--name"], ct);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"virsh list failed ({r.ExitCode}): {r.Stderr.Trim()}");

        var found = new Dictionary<string, VmHandle>();
        foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (LibvirtPaths.TryParse(line) is { } h) found[h.Name] = h;

        var root = LibvirtPaths.RunRoot(_o);
        if (Directory.Exists(root))
            foreach (var dir in Directory.EnumerateDirectories(root, "maldex-*"))
                if (LibvirtPaths.TryParse(Path.GetFileName(dir)) is { } h) found.TryAdd(h.Name, h);

        return [.. found.Values];
    }

    private static int FindFreeVncPort()
    {
        for (var port = 5900; port <= 5999; port++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                l.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new VmStartException("no free VNC port in 5900-5999");
    }
}

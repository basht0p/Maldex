using System.Globalization;
using System.Text.RegularExpressions;
using Maldex.Core.Abstractions;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Execution;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Net;

/// <summary>Timeouts, limits and host paths the capture service uses. Tests shrink these; production uses the defaults.</summary>
public sealed record CaptureTuning
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>How long tcpdump has to start capturing (its output file appears once the capture is active).</summary>
    public TimeSpan StartWait { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>How long tcpdump has to flush and exit after SIGINT before it is killed.</summary>
    public TimeSpan StopWait { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>RLIMIT_FSIZE for tcpdump: a flooding guest cannot fill the disk; the file ends as a valid prefix.</summary>
    public long MaxPcapBytes { get; init; } = CaptureService.DefaultMaxPcapBytes;
    public long MaxFlowsBytes { get; init; } = NetnsEgressRouter.MaxFlowsBytes;
    /// <summary>The unprivileged account tcpdump drops to; it must be the one that owns the capture directory.</summary>
    public string? RunAs { get; init; }
    public Func<Guid, string> EnablePath { get; init; } = NetnsEgressRouter.MitmCaptureEnablePath;
    public Func<Guid, string> FlowsPath { get; init; } = NetnsEgressRouter.MitmFlowsPath;
}

/// <summary>
/// Real <see cref="ICaptureService"/>: <c>tcpdump</c> on the namespace leg of the run's veth plus the mitmproxy flow log.
/// <para>
/// tcpdump runs as root only until it has opened the interface: <c>-Z</c> drops it to the control-plane user BEFORE it creates
/// the output file, and the file lives in a per-run directory (<c>{RunDir}/net/{ns}/cap</c>, mode 0700) that only that user can
/// write. So no less-trusted account can plant a link or a replacement there, and the control plane reads its own file with
/// plain I/O. With <c>-w</c> tcpdump only copies raw frames (it does not dissect them), which keeps the hostile-input surface
/// of the process that runs as the control-plane user small. The mitm flow log, by contrast, is written by the mitm user, so it
/// is copied out with <see cref="UntrustedFile"/>, never opened directly.
/// </para>
/// </summary>
public sealed partial class CaptureService(
    IOptions<NetOptions> options, IProcessRunner runner, Action<string>? warn = null, CaptureTuning? tuning = null)
    : ICaptureService
{
    /// <summary>A pcap with only its 24-byte global header holds no packets.</summary>
    internal const int PcapHeaderBytes = 24;

    /// <summary>Default size limit of a run's pcap (also the largest pcap the coordinator stores).</summary>
    public const long DefaultMaxPcapBytes = 1L << 30;

    private readonly NetOptions _o = options.Value;
    private readonly IProcessRunner _runner = runner;
    private readonly Action<string>? _warn = warn;
    private readonly CaptureTuning _t = tuning ?? new CaptureTuning();

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UserRegex();

    public async Task<ICaptureHandle> StartAsync(NetworkLease lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var names = RunNetNames.For(lease.TaskId);
        var user = _t.RunAs ?? Environment.UserName;
        if (!UserRegex().IsMatch(user)) throw new CaptureStartException($"unusable capture account name '{user}'");

        var capDir = Path.Combine(Path.GetFullPath(_o.RunDir), "net", names.Namespace, "cap");
        var pcap = Path.Combine(capDir, "capture.pcap");
        var handle = new Handle(this, lease.TaskId, capDir, pcap);
        try
        {
            Directory.CreateDirectory(capDir);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(capDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (File.Exists(pcap)) File.Delete(pcap);

            var (file, args) = PrivilegedCommands.DetachedInNs(names.Namespace, Path.Combine(capDir, "tcpdump.log"),
                "prlimit", $"--fsize={_t.MaxPcapBytes.ToString(CultureInfo.InvariantCulture)}", "--",
                "tcpdump", "-n", "--immediate-mode", "-i", names.VethNs, "-U", "-s", "0", "-Z", user, "-w", pcap);
            var launched = await _runner.RunAsync(file, args, ct).ConfigureAwait(false);
            if (launched.ExitCode != 0) throw new CaptureStartException("tcpdump could not be launched: " + Tail(launched.Stderr));

            // The output file appears only after tcpdump has activated the capture (and dropped privileges).
            var deadline = DateTime.UtcNow + _t.StartWait;
            while (!File.Exists(pcap))
            {
                if (DateTime.UtcNow >= deadline)
                    throw new CaptureStartException("tcpdump did not start capturing in time" + LogTail(capDir));
                await Task.Delay(_t.PollInterval, ct).ConfigureAwait(false);
            }

            handle.Pids = await FindTcpdumpAsync(names.Namespace, pcap, ct).ConfigureAwait(false);
            if (handle.Pids.Count == 0) throw new CaptureStartException("tcpdump exited right after starting" + LogTail(capDir));

            // Flow logging is switched on only now: everything from here on is also in the pcap. The router creates the ctl
            // directory (owned by the control-plane user); without it this run has no mitm proxy and so no flows.
            var enable = _t.EnablePath(lease.TaskId);
            if (Directory.Exists(Path.GetDirectoryName(enable)!))
            {
                await File.WriteAllTextAsync(enable, "", ct).ConfigureAwait(false);
                handle.FlowsEnabled = true;
            }
            else
            {
                _warn?.Invoke($"No mitm control directory for {lease.TaskId:N}; HTTP flows are not recorded.");
            }
            return handle;
        }
        catch (Exception ex)
        {
            // tcpdump may be running without having created its file yet: find it so it is not left behind.
            if (handle.Pids.Count == 0)
            {
                try { handle.Pids = await FindTcpdumpAsync(names.Namespace, pcap, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* best effort */ }
            }
            await handle.DisposeAsync().ConfigureAwait(false);
            if (ex is OperationCanceledException or CaptureStartException) throw;
            throw new CaptureStartException("capture could not be started: " + ex.Message, ex);
        }
    }

    private static string Tail(string s) => s.Length <= 300 ? s.Trim() : s[^300..].Trim();

    private static string LogTail(string capDir)
    {
        try
        {
            var log = Path.Combine(capDir, "tcpdump.log");
            return File.Exists(log) ? ": " + Tail(File.ReadAllText(log)) : "";
        }
        catch (IOException) { return ""; }
    }

    /// <summary>The PIDs in the namespace that are our tcpdump (the namespace also hosts dnsmasq, mitmdump, INetSim...).</summary>
    private async Task<IReadOnlyList<int>> FindTcpdumpAsync(string ns, string pcap, CancellationToken ct)
    {
        var (f, a) = PrivilegedCommands.Sudo("ip", "netns", "pids", ns);
        var pids = await _runner.RunAsync(f, a, ct).ConfigureAwait(false);
        if (pids.ExitCode != 0) return [];
        var found = new List<int>();
        foreach (var token in pids.Stdout.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && await IsOurTcpdumpAsync(pid, pcap, ct).ConfigureAwait(false))
                found.Add(pid);
        return found;
    }

    /// <summary>
    /// True when the process still is our tcpdump. Matches on the NUL-separated argv from /proc (a path with spaces cannot
    /// confuse it) and requires the exact pcap path, so a recycled PID is never mistaken for it.
    /// </summary>
    private async Task<bool> IsOurTcpdumpAsync(int pid, string pcap, CancellationToken ct)
    {
        var r = await _runner.RunAsync("cat", [$"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/cmdline"], ct).ConfigureAwait(false);
        if (r.ExitCode != 0) return false;
        var argv = r.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return argv.Length > 0 && Path.GetFileName(argv[0]) == "tcpdump" && argv.Contains(pcap, StringComparer.Ordinal);
    }

    private sealed class Handle(CaptureService svc, Guid taskId, string capDir, string pcap) : ICaptureHandle
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IReadOnlyList<CapturedFile>? _result;
        private bool _disposed;

        private readonly List<string> _warnings = [];
        private bool _exitedEarly;

        public IReadOnlyList<string> Warnings { get { lock (_warnings) return _warnings.ToArray(); } }
        public IReadOnlyList<int> Pids { get; set; } = [];
        public bool FlowsEnabled { get; set; }

        public async Task<IReadOnlyList<CapturedFile>> StopAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return _result ??= await StopCoreAsync(ct).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        private async Task<IReadOnlyList<CapturedFile>> StopCoreAsync(CancellationToken ct)
        {
            var t = svc._t;
            // Flow logging off first, so nothing is appended while the file is copied.
            DisableFlows();
            await StopTcpdumpAsync(ct).ConfigureAwait(false);

            var files = new List<CapturedFile>();
            var pcapLength = File.Exists(pcap) ? new FileInfo(pcap).Length : 0;
            if (pcapLength > PcapHeaderBytes) files.Add(new CapturedFile(ArtifactKind.Pcap, pcap));
            if (pcapLength >= t.MaxPcapBytes) Warn("pcap truncated at the size limit");
            else if (_exitedEarly) Warn("tcpdump exited early; the pcap may be incomplete");

            var flows = Path.Combine(capDir, "flows.jsonl");
            try
            {
                // Written by the mitm user: never opened in place, only copied by UntrustedFile.
                var res = await UntrustedFile.CopyRegularFileAsync(t.FlowsPath(taskId), flows, t.MaxFlowsBytes, ct).ConfigureAwait(false);
                if (res.Truncated) Warn($"flows truncated at {t.MaxFlowsBytes} bytes");
                if (res.BytesCopied > 0) files.Add(new CapturedFile(ArtifactKind.MitmFlows, flows));
                else File.Delete(flows);
            }
            catch (FileNotFoundException) { /* no flow was logged */ }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Warn($"could not copy the flows: {ex.Message}");
                try { File.Delete(flows); } catch (IOException) { }
            }
            return files;
        }

        private void DisableFlows()
        {
            if (!FlowsEnabled) return;
            FlowsEnabled = false;
            try { File.Delete(svc._t.EnablePath(taskId)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                svc._warn?.Invoke($"Could not disable flow logging for {taskId:N}: {ex.Message}");
            }
        }

        /// <summary>
        /// SIGINT lets tcpdump flush and exit; whatever is still there after the grace period is killed. Every signal is sent
        /// only after the PID has been re-verified as our tcpdump (it may have died early and the PID been recycled).
        /// </summary>
        private async Task StopTcpdumpAsync(CancellationToken ct)
        {
            if (Pids.Count == 0) return;
            var running = await VerifiedAsync().ConfigureAwait(false);
            _exitedEarly = running.Count == 0;
            foreach (var pid in running) await SignalAsync("-INT", pid).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + svc._t.StopWait;
            List<int> alive;
            while ((alive = await VerifiedAsync().ConfigureAwait(false)).Count > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(svc._t.PollInterval, ct).ConfigureAwait(false);
            foreach (var pid in alive)
            {
                Warn($"tcpdump {pid} did not exit after SIGINT and was killed");
                await SignalAsync("-KILL", pid).ConfigureAwait(false);
            }
            Pids = [];
        }

        private async Task<List<int>> VerifiedAsync()
        {
            var ok = new List<int>();
            foreach (var pid in Pids)
                if (await svc.IsOurTcpdumpAsync(pid, pcap, CancellationToken.None).ConfigureAwait(false)) ok.Add(pid);
            return ok;
        }

        private async Task SignalAsync(string signal, int pid)
        {
            // Re-verify immediately before the root signal.
            if (!await svc.IsOurTcpdumpAsync(pid, pcap, CancellationToken.None).ConfigureAwait(false)) return;
            var (f, a) = PrivilegedCommands.Sudo("kill", signal, pid.ToString(CultureInfo.InvariantCulture));
            await svc._runner.RunAsync(f, a, CancellationToken.None).ConfigureAwait(false);
        }

        private void Warn(string message)
        {
            lock (_warnings) _warnings.Add(message);
            svc._warn?.Invoke($"{message} (task {taskId:N})");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_result is null)
                {
                    DisableFlows();
                    await StopTcpdumpAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                svc._warn?.Invoke($"Stopping the capture of {taskId:N} failed: {ex.Message}");
            }
            try { if (Directory.Exists(capDir)) Directory.Delete(capDir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                svc._warn?.Invoke($"Could not delete the capture files of {taskId:N}: {ex.Message}");
            }
        }
    }
}

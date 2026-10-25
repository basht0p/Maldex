using System.Text.Json;
using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Core.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maldex.Orchestrator.Templates.Build;

/// <summary>What a finished provisioning reported about the new guest.</summary>
public sealed record ProvisionReport(string WindowsEdition, string WindowsBuild, bool? OfficePresent, bool? RealTimeProtectionEnabled);

/// <summary>A Build could not install or provision Windows. The message is safe to store as the job's failure reason.</summary>
public sealed class TemplateBuildException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The Installing and Provisioning phases of a Build: waits while Windows Setup installs from the attached media until the Maldex
/// agent (started by the unattended setup) answers, waits for Provision.ps1's result marker, keeps its log, checks the outcome
/// and powers the guest off cleanly. Starting the VM with the media and destroying it afterwards are the caller's.
/// </summary>
public sealed class TemplateInstaller(
    IHypervisor hypervisor,
    IAgentChannelFactory channels,
    IOptions<TemplateJobOptions> jobOptions,
    IOptions<BuildOptions> buildOptions,
    TimeProvider time,
    ILogger<TemplateInstaller> logger)
{
    public const string ProvisioningNote = "installing drivers, Sysmon, policies and applications";
    public const int MaxResultBytes = 4096;
    public const int MaxFailedSteps = 20;
    private const string BootKey = "KEY_SPACE";
    private const string SetupStopped = "Windows Setup stopped before the Maldex agent started (check the media and edition)";
    private const string StoppedDuringProvisioning = "the guest stopped before provisioning finished";
    private const string UnreadableResult = "the provisioning result could not be read";
    /// <summary>How long one <c>collect_file</c> (the result marker or the log) may take before it is given up.</summary>
    private static readonly TimeSpan CollectTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Runs Installing, then Provisioning (announced through <paramref name="enterPhase"/>), on the started <paramref name="vm"/>.</summary>
    /// <exception cref="TemplateBuildException">Any failure; the message names it.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled, whatever the interrupted call threw (R32).</exception>
    public async Task<ProvisionReport> InstallAsync(VmHandle vm, Guid jobId, bool officeSelected, Func<TemplateJobState, string, Task> enterPhase, CancellationToken ct)
    {
        try
        {
            return await RunAsync(vm, jobId, officeSelected, enterPhase, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // Cancellation is decided by the token, never by the exception's type.
            throw new OperationCanceledException("the build was cancelled", ex, ct);
        }
    }

    private async Task<ProvisionReport> RunAsync(VmHandle vm, Guid jobId, bool officeSelected, Func<TemplateJobState, string, Task> enterPhase, CancellationToken ct)
    {
        var o = jobOptions.Value;
        var b = buildOptions.Value;

        await using var ch = await ConnectAsync(vm, ct).ConfigureAwait(false);
        await using var session = new GuestSession(ch, time);

        await AwaitAgentAsync(vm, session, o, b, ct).ConfigureAwait(false);
        await AskProvenanceAsync(session, o, ct).ConfigureAwait(false);

        await enterPhase(TemplateJobState.Provisioning, ProvisioningNote).ConfigureAwait(false);
        var marker = await AwaitResultAsync(vm, session, o, b, ct).ConfigureAwait(false);
        await KeepLogAsync(session, b, jobId, ct).ConfigureAwait(false);

        var failedSteps = ParseResult(marker);
        if (failedSteps is not null)
            throw new TemplateBuildException($"provisioning failed in the guest: {string.Join(", ", failedSteps)}; see the provisioning log");

        var p = await AskProvenanceAsync(session, o, ct).ConfigureAwait(false);
        if (officeSelected && p.OfficePresent != true)
            throw new TemplateBuildException("Office was selected but is not installed; see the provisioning log");

        if (!await GuestPowerOff.PowerOffAsync(session, ch, hypervisor, vm, o, time, (m, inner) => new TemplateBuildException(m, inner), ct).ConfigureAwait(false))
            throw new TemplateBuildException("the guest did not power off after provisioning");

        return new ProvisionReport(GuestText.Clean(p.WindowsEdition, 200), GuestText.Clean(p.WindowsBuild, 100), p.OfficePresent, p.RealTimeProtectionEnabled);
    }

    private async Task<IAgentChannel> ConnectAsync(VmHandle vm, CancellationToken ct)
    {
        try
        {
            return await channels.ConnectAsync(vm, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new TemplateBuildException($"could not connect to the guest agent ({GuestText.Clean(ex.Message, 200)})", ex);
        }
    }

    /// <summary>
    /// Waits for the agent's ready message while Windows installs, bounded by <see cref="BuildOptions.InstallTimeout"/>, and fails at
    /// once when the VM stops. Waiting for ready is sound here: the host is connected before the agent's first session starts.
    /// Meanwhile the console is sent a key now and then, for the "press any key to boot from CD" prompt.
    /// </summary>
    private async Task AwaitAgentAsync(VmHandle vm, GuestSession session, TemplateJobOptions o, BuildOptions b, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(b.InstallTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        using var stopTapping = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        var tapping = TapBootKeyAsync(vm, b, stopTapping.Token);
        try
        {
            while (!session.Ready.IsCompleted)
            {
                if (!await IsRunningAsync(vm, linked.Token).ConfigureAwait(false)) throw new TemplateBuildException(SetupStopped);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                await Task.WhenAny(session.Ready, Task.Delay(o.PollInterval, time, wait.Token)).ConfigureAwait(false);
                await wait.CancelAsync().ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
            }
            await session.Ready.ConfigureAwait(false);
        }
        catch (Exception) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TemplateBuildException($"Windows was not installed within {Duration(b.InstallTimeout)} (the Maldex agent never started)");
        }
        catch (InvalidOperationException) when (!linked.IsCancellationRequested && session.Ready.IsFaulted)
        {
            // The connection ended before the agent said ready.
            if (!await IsRunningAsync(vm, ct).ConfigureAwait(false)) throw new TemplateBuildException(SetupStopped);
            throw new TemplateBuildException("the agent connection closed before the Maldex agent started");
        }
        finally
        {
            await stopTapping.CancelAsync().ConfigureAwait(false);
            await tapping.ConfigureAwait(false);
        }
    }

    /// <summary>Presses <see cref="BootKey"/> <see cref="BuildOptions.BootKeyTaps"/> times, one every <see cref="BuildOptions.BootKeyInterval"/>, until stopped. Never throws.</summary>
    private async Task TapBootKeyAsync(VmHandle vm, BuildOptions b, CancellationToken ct)
    {
        for (var i = 0; i < b.BootKeyTaps; i++)
        {
            try
            {
                await hypervisor.SendKeyAsync(vm, BootKey, ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Best effort: a missed key only matters if the media waits for one, and then the install timeout reports it.
                logger.LogWarning(ex, "Sending {Key} to {Vm} failed.", BootKey, vm.Name);
            }
            try
            {
                await Task.Delay(b.BootKeyInterval, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One provenance round trip (resent while the agent reconnects), bounded by <see cref="TemplateJobOptions.ProvenanceTimeout"/>.</summary>
    private async Task<ProvenanceMessage> AskProvenanceAsync(GuestSession session, TemplateJobOptions o, CancellationToken ct)
    {
        try
        {
            return await session.AskProvenanceWithRetryAsync(o.ProvenanceRetryInterval, o.ProvenanceTimeout, time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!ct.IsCancellationRequested)
        {
            throw new TemplateBuildException($"the guest agent did not answer: no reply within {o.ProvenanceTimeout.TotalSeconds:0} s");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException && !ct.IsCancellationRequested)
        {
            throw new TemplateBuildException($"the guest agent did not answer: {GuestText.Clean(ex.Message, 200)}", ex);
        }
    }

    /// <summary>
    /// Polls for the result marker every <see cref="BuildOptions.ProvisionPollInterval"/>, bounded by
    /// <see cref="BuildOptions.ProvisionTimeout"/>; fails at once when the VM stops. A poll the agent does not answer (it may be
    /// restarting with the guest) is simply repeated.
    /// </summary>
    private async Task<byte[]> AwaitResultAsync(VmHandle vm, GuestSession session, TemplateJobOptions o, BuildOptions b, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(b.ProvisionTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            while (true)
            {
                if (!await IsRunningAsync(vm, linked.Token).ConfigureAwait(false)) throw new TemplateBuildException(StoppedDuringProvisioning);
                byte[]? marker = null;
                try
                {
                    marker = await session.CollectFileAsync(BuildOptions.ProvisionResultGuestPath, MaxResultBytes, CollectTimeout, linked.Token).ConfigureAwait(false);
                }
                catch (TimeoutException) when (!linked.IsCancellationRequested)
                {
                    // Not answered this time; ask again at the next poll.
                }
                catch (Exception ex) when (!linked.IsCancellationRequested && ex is ChannelClosedException or ObjectDisposedException or IOException)
                {
                    if (!await IsRunningAsync(vm, ct).ConfigureAwait(false)) throw new TemplateBuildException(StoppedDuringProvisioning);
                    throw new TemplateBuildException($"the guest agent disconnected during provisioning ({GuestText.Clean(ex.Message, 200)})", ex);
                }
                catch (InvalidOperationException ex) when (!linked.IsCancellationRequested)
                {
                    logger.LogWarning("The provisioning result was rejected: {Problem}", GuestText.Clean(ex.Message, 300));
                    throw new TemplateBuildException(UnreadableResult, ex);
                }
                if (marker is not null) return marker;
                await Task.Delay(b.ProvisionPollInterval, time, linked.Token).ConfigureAwait(false);
            }
        }
        catch (Exception) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TemplateBuildException($"provisioning did not finish within {Duration(b.ProvisionTimeout)}");
        }
    }

    /// <summary>
    /// Copies provision.log (its last <see cref="BuildOptions.ProvisionLogMaxBytes"/> when longer) to
    /// <c>&lt;JobLogDir&gt;/&lt;jobId:N&gt;/provision.log</c>, readable by the service account only. Best effort: a log that cannot be
    /// fetched or written is logged, never a failure of its own.
    /// </summary>
    private async Task KeepLogAsync(GuestSession session, BuildOptions b, Guid jobId, CancellationToken ct)
    {
        byte[]? log;
        try
        {
            log = await session.CollectFileAsync(BuildOptions.ProvisionLogGuestPath, AgentLimits.MaxDroppedFileBytes, CollectTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The provisioning log of build {JobId} could not be collected.", jobId);
            return;
        }
        if (log is null)
        {
            logger.LogWarning("The guest has no provisioning log for build {JobId}.", jobId);
            return;
        }

        var keep = log.Length > b.ProvisionLogMaxBytes ? log.AsMemory(log.Length - b.ProvisionLogMaxBytes) : log.AsMemory();
        var dir = Path.Combine(b.JobLogDir, jobId.ToString("N"));
        var path = Path.Combine(dir, "provision.log");
        try
        {
            Directory.CreateDirectory(dir);
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(path, options))
                await stream.WriteAsync(keep, ct).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // also when it existed
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The provisioning log of build {JobId} could not be written to {Path}.", jobId, path);
        }
    }

    /// <summary>
    /// Null when provisioning succeeded; otherwise the failed steps (cleaned). The marker must be exactly an object with a bool
    /// <c>ok</c> and a <c>failedSteps</c> array of at most <see cref="MaxFailedSteps"/> strings.
    /// </summary>
    /// <exception cref="TemplateBuildException">The marker has any other shape.</exception>
    private static IReadOnlyList<string>? ParseResult(byte[] marker)
    {
        try
        {
            using var doc = JsonDocument.Parse(marker);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("failedSteps", out var steps) || steps.ValueKind != JsonValueKind.Array
                || steps.GetArrayLength() > MaxFailedSteps
                || steps.EnumerateArray().Any(s => s.ValueKind != JsonValueKind.String))
                throw new TemplateBuildException(UnreadableResult);
            return ok.GetBoolean() ? null : steps.EnumerateArray().Select(s => GuestText.Clean(s.GetString(), 100)).ToList();
        }
        catch (JsonException ex)
        {
            throw new TemplateBuildException(UnreadableResult, ex);
        }
    }

    private async Task<bool> IsRunningAsync(VmHandle vm, CancellationToken ct)
    {
        try
        {
            return await hypervisor.IsRunningAsync(vm, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new TemplateBuildException($"could not check whether the guest is running: {GuestText.Clean(ex.Message, 200)}", ex);
        }
    }

    private static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 && t.TotalHours % 1 == 0 ? $"{t.TotalHours:0} h"
        : t.TotalMinutes >= 1 && t.TotalMinutes % 1 == 0 ? $"{t.TotalMinutes:0} min"
        : $"{t.TotalSeconds:0} s";
}

using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Text;
using Microsoft.Extensions.Options;

namespace Maldex.Orchestrator.Templates;

/// <summary>
/// Seals a template working copy: installs the current Maldex components into the running guest, powers it off, and only
/// when the guest has really stopped makes the disk read-only. A disk that may still be written to is never sealed.
/// </summary>
public sealed class TemplateSealer(IHypervisor hypervisor, ITemplateStore store, IOptions<TemplateJobOptions> options, TimeProvider time)
{
    private const string DidNotPowerOff = "the guest did not power off; not sealing a possibly dirty disk";

    /// <summary>
    /// Checks the agent answers on <paramref name="ch"/> (a provenance round trip bounded by ProvenanceTimeout; ready is not needed),
    /// delivers the agent exe, Sysmon config and CA, applies them, powers the guest off and seals <paramref name="stem"/>.
    /// The caller owns <paramref name="ch"/> and <paramref name="vm"/> and releases them afterwards.
    /// </summary>
    /// <exception cref="TemplateSealException">Any failure; the disk is then left unsealed.</exception>
    public async Task SealAsync(IAgentChannel ch, VmHandle vm, string stem, ComponentBundle b, CancellationToken ct)
    {
        var o = options.Value;
        await using var session = new GuestSession(ch);

        // Liveness is proven by a round trip, never by waiting for ready: the agent says ready once per session, and a host that
        // connects later (or reconnects) may never see it. Queued ready messages and other frames are skipped by the session.
        // A request sent while the agent is reconnecting is lost, so it is resent (see AskProvenanceWithRetryAsync).
        try
        {
            await session.AskProvenanceWithRetryAsync(o.ProvenanceRetryInterval, o.ProvenanceTimeout, time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TemplateSealException($"the guest agent did not answer: no reply within {o.ProvenanceTimeout.TotalSeconds:0} s; not sealing");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException or ChannelClosedException)
        {
            throw new TemplateSealException($"the guest agent did not answer: {GuestText.Clean(ex.Message, 200)}; not sealing", ex);
        }

        var delivery = new SampleDelivery(time, o.ChunkTimeout);
        var agentId = $"{AgentLimits.ComponentTransferPrefix}agent/{b.AgentSha256}";
        var sysmonId = $"{AgentLimits.ComponentTransferPrefix}sysmon/{b.SysmonConfigSha256}";
        var caId = $"{AgentLimits.ComponentTransferPrefix}ca/{b.MitmCaSha256}";
        Func<string, Exception> fail = m => new TemplateSealException("component delivery failed: " + m);

        await delivery.DeliverComponentAsync(ch, session.Inbox, caId, b.CaDer, b.MitmCaSha256, fail, ct).ConfigureAwait(false);
        await delivery.DeliverComponentAsync(ch, session.Inbox, sysmonId, b.SysmonConfig, b.SysmonConfigSha256, fail, ct).ConfigureAwait(false);
        await delivery.DeliverComponentAsync(ch, session.Inbox, agentId, b.AgentExe, b.AgentSha256, fail, ct).ConfigureAwait(false);

        await delivery.CommandAsync(ch, session.Inbox, new ApplyComponentsCommand(NewId(), agentId, sysmonId, caId), "apply_components",
            o.ApplyComponentsTimeout, m => new TemplateSealException("installing the components failed: " + m), ct).ConfigureAwait(false);

        // One deadline covers the power_off acknowledgement and the wait for the guest to stop running.
        using var deadline = new CancellationTokenSource(o.PowerOffTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        await AwaitPowerOffAckAsync(ch, session, linked.Token).ConfigureAwait(false);
        var stopped = await WaitUntilStoppedAsync(vm, o, linked.Token, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!stopped) throw new TemplateSealException(DidNotPowerOff);

        // Clears whatever remains of a shut-off domain (idempotent); then the disk can no longer change.
        try
        {
            await hypervisor.DestroyAsync(vm, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TemplateSealException($"cleaning up the powered-off guest failed: {GuestText.Clean(ex.Message, 200)}", ex);
        }
        await store.SealAsync(stem, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends power_off and waits for its result. A refusal fails. A disconnect (the OS went away before or while answering) counts
    /// as acknowledged, and so does running out of time: whether the guest really stopped is the hypervisor's word, checked next.
    /// </summary>
    private static async Task AwaitPowerOffAckAsync(IAgentChannel ch, GuestSession session, CancellationToken ct)
    {
        var command = new PowerOffCommand(NewId());
        using var lease = session.Inbox.Open();
        try
        {
            await ch.SendAsync(command, ct).ConfigureAwait(false);
            while (true)
            {
                var message = await lease.Reader.ReadAsync(ct).ConfigureAwait(false);
                if (message is not CommandResultMessage result || result.CommandId != command.CommandId) continue;
                if (!result.Success) throw new TemplateSealException($"powering off failed: the agent refused it ({GuestText.Clean(result.Error, 200)})");
                return;
            }
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Disconnected, or out of time: the running-wait decides.
        }
    }

    /// <summary>
    /// True once the hypervisor says the guest is not running; false when the deadline passes first. <paramref name="ct"/> is the
    /// deadline linked to the caller's token; <paramref name="callerCt"/> is the caller's alone (the last look honours it too).
    /// </summary>
    private async Task<bool> WaitUntilStoppedAsync(VmHandle vm, TemplateJobOptions o, CancellationToken ct, CancellationToken callerCt)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await hypervisor.IsRunningAsync(vm, ct).ConfigureAwait(false)) return true;
                await Task.Delay(o.PollInterval, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                throw new TemplateSealException($"could not check whether the guest stopped: {GuestText.Clean(ex.Message, 200)}", ex);
            }
        }

        // Out of time: one short last look, so a guest that stopped just now is not reported as dirty.
        using var lastTimeout = new CancellationTokenSource(o.PollInterval * 5, time);
        using var last = CancellationTokenSource.CreateLinkedTokenSource(callerCt, lastTimeout.Token);
        try
        {
            return !await hypervisor.IsRunningAsync(vm, last.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested && lastTimeout.IsCancellationRequested)
        {
            return false; // the last look itself ran out of time: it did not power off
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !callerCt.IsCancellationRequested)
        {
            throw new TemplateSealException($"could not check whether the guest stopped: {GuestText.Clean(ex.Message, 200)}", ex);
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}

using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Text;
using Maldex.Orchestrator.Templates.Build;
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
        catch (TimeoutException) when (!ct.IsCancellationRequested)
        {
            throw new TemplateSealException($"the guest agent did not answer: no reply within {o.ProvenanceTimeout.TotalSeconds:0} s; not sealing");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException or ChannelClosedException && !ct.IsCancellationRequested)
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
        var stopped = await GuestPowerOff.PowerOffAsync(session, ch, hypervisor, vm, o, time, (m, inner) => new TemplateSealException(m, inner), ct)
            .ConfigureAwait(false);
        if (!stopped) throw new TemplateSealException(DidNotPowerOff);

        // Clears whatever remains of a shut-off domain (idempotent); then the disk can no longer change.
        try
        {
            await hypervisor.DestroyAsync(vm, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new TemplateSealException($"cleaning up the powered-off guest failed: {GuestText.Clean(ex.Message, 200)}", ex);
        }
        await store.SealAsync(stem, ct).ConfigureAwait(false);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}

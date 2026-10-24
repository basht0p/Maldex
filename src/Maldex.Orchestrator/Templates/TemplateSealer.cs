using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Text;
using Microsoft.Extensions.Options;

namespace Maldex.Orchestrator.Templates;

/// <summary>
/// Seals a template working copy: installs the current Maldex components into the running guest, powers it off, and only
/// when the guest is really gone makes the disk read-only. A disk that may still be written to is never sealed.
/// </summary>
public sealed class TemplateSealer(IHypervisor hypervisor, ITemplateStore store, IOptions<TemplateJobOptions> options, TimeProvider time)
{
    /// <summary>
    /// Waits for the agent's ready message on <paramref name="ch"/> (this reads the channel; the caller must not have consumed it),
    /// delivers the agent exe, Sysmon config and CA, applies them, powers the guest off and seals <paramref name="stem"/>.
    /// The caller owns <paramref name="ch"/> and <paramref name="vm"/> and releases them afterwards.
    /// </summary>
    /// <exception cref="TemplateSealException">Any failure; the disk is then left unsealed.</exception>
    public async Task SealAsync(IAgentChannel ch, VmHandle vm, string stem, ComponentBundle b, CancellationToken ct)
    {
        var o = options.Value;
        await using var session = new GuestSession(ch);

        try
        {
            await session.Ready.WaitAsync(o.AgentReadyTimeout, time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TemplateSealException($"the guest agent was not ready within {o.AgentReadyTimeout.TotalSeconds:0} s; not sealing");
        }
        catch (InvalidOperationException ex)
        {
            throw new TemplateSealException($"the guest agent never became ready ({ex.Message}); not sealing");
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

        await delivery.CommandAsync(ch, session.Inbox, new PowerOffCommand(NewId()), "power_off",
            o.PowerOffTimeout, m => new TemplateSealException("powering off failed: " + m), ct).ConfigureAwait(false);

        if (!await WaitUntilGoneAsync(vm, o, ct).ConfigureAwait(false))
            throw new TemplateSealException("the guest did not power off; not sealing a possibly dirty disk");

        // Clears whatever remains of a shut-off domain (idempotent); then the disk can no longer change.
        await hypervisor.DestroyAsync(vm, ct).ConfigureAwait(false);
        await store.SealAsync(stem, ct).ConfigureAwait(false);
    }

    private async Task<bool> WaitUntilGoneAsync(VmHandle vm, TemplateJobOptions o, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(o.PowerOffTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            while (true)
            {
                var running = await hypervisor.ListAsync(linked.Token).ConfigureAwait(false);
                if (running.All(h => h.Name != vm.Name)) return true;
                await Task.Delay(o.PollInterval, time, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}

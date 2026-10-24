using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Microsoft.Extensions.Options;

namespace Maldex.Orchestrator.Templates;

/// <summary>
/// Boots a throwaway overlay of a sealed template (no network), asks the agent for its provenance and checks that the disk
/// carries the current agent and has Defender real-time protection off. The VM is always destroyed.
/// </summary>
public sealed class TemplateVerifier(IHypervisor hypervisor, IAgentChannelFactory channels, IEgressRouter egress, IOptions<OrchestratorOptions> orchestrator, IOptions<TemplateJobOptions> options, TimeProvider time)
{
    public const string DefenderUnreadableNote = "Defender state could not be read";

    /// <exception cref="TemplateVerifyException">The disk failed verification or never answered.</exception>
    public async Task<VerifiedProvenance> VerifyAsync(string stem, ComponentBundle b, CancellationToken ct)
    {
        var o = options.Value;
        var taskId = Guid.NewGuid();
        NetworkLease? lease = null;
        VmHandle? vm = null;
        IAgentChannel? channel = null;
        try
        {
            try
            {
                lease = await egress.CreateAsync(taskId, NetworkMode.None, null, ct).ConfigureAwait(false);
                vm = await hypervisor.StartAsync(new VmSpec(taskId, stem, orchestrator.Value.VmMemoryMiB, orchestrator.Value.VmVCpus, lease.NetworkId), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is VmStartException or EgressUnavailableException)
            {
                throw new TemplateVerifyException($"the sealed disk could not be booted: {ex.Message}");
            }

            channel = await channels.ConnectAsync(vm, ct).ConfigureAwait(false);
            await using var session = new GuestSession(channel);

            // One budget for "ready" and "provenance" together.
            using var budget = new CancellationTokenSource(o.VerifyTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            ProvenanceMessage p;
            try
            {
                try
                {
                    await session.Ready.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TemplateVerifyException($"the sealed guest agent did not become ready within {o.VerifyTimeout.TotalSeconds:0} s");
                }

                try
                {
                    p = await session.AskProvenanceAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TemplateVerifyException($"no provenance answer from the sealed guest agent within {o.VerifyTimeout.TotalSeconds:0} s");
                }
            }
            catch (InvalidOperationException ex)
            {
                throw new TemplateVerifyException($"provenance check failed: {ex.Message}");
            }

            if (p.AgentVersion != b.AgentBuildId) throw new TemplateVerifyException("the sealed agent is not the current build");
            if (p.RealTimeProtectionEnabled == true) throw new TemplateVerifyException("Defender real-time protection is on; turn it off in an Edit session");

            return new VerifiedProvenance(p.WindowsEdition, p.WindowsBuild, p.AgentVersion, p.OfficePresent, p.RealTimeProtectionEnabled,
                p.RealTimeProtectionEnabled is null ? DefenderUnreadableNote : null);
        }
        finally
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            if (vm is not null) await hypervisor.DestroyAsync(vm, CancellationToken.None).ConfigureAwait(false);
            if (lease is not null) await egress.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

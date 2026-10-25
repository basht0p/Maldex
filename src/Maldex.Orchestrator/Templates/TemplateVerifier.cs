using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Infrastructure.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Maldex.Orchestrator.Templates;

/// <summary>
/// Boots a throwaway overlay of a sealed template (no network), asks the agent for its provenance and checks that the disk
/// carries the current agent and has Defender real-time protection off. The VM is always destroyed.
/// </summary>
public sealed class TemplateVerifier(IHypervisor hypervisor, IAgentChannelFactory channels, IEgressRouter egress, IOptions<OrchestratorOptions> orchestrator,
    IOptions<TemplateJobOptions> options, IOptions<NetOptions> net, TimeProvider time, ILogger<TemplateVerifier>? logger = null)
{
    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;

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
            catch (Exception ex) when (ex is VmStartException or EgressUnavailableException && !ct.IsCancellationRequested)
            {
                throw new TemplateVerifyException($"the sealed disk could not be booted: {ex.Message}");
            }

            try
            {
                channel = await channels.ConnectAsync(vm, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new TemplateVerifyException($"could not connect to the sealed guest agent: {ex.Message}");
            }
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
                catch (Exception) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TemplateVerifyException($"the sealed guest agent did not become ready within {o.VerifyTimeout.TotalSeconds:0} s");
                }

                try
                {
                    // Resent until answered: a request sent while the agent reconnects is lost (as for the sealer and the job's check).
                    p = await session.AskProvenanceWithRetryAsync(o.ProvenanceRetryInterval, o.VerifyTimeout, time, linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && (ex is TimeoutException || budget.IsCancellationRequested))
                {
                    throw new TemplateVerifyException($"no provenance answer from the sealed guest agent within {o.VerifyTimeout.TotalSeconds:0} s");
                }
            }
            catch (InvalidOperationException ex) when (!ct.IsCancellationRequested)
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
            // Each step on its own: a failing one is logged and never skips the others (nor hides the verification's outcome).
            if (channel is not null)
            {
                try { await channel.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _log.LogWarning(ex, "Closing the agent channel of verification boot {TaskId} failed.", taskId); }
            }
            if (vm is not null)
            {
                try { await hypervisor.DestroyAsync(vm, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _log.LogError(ex, "Destroying verification VM {Vm} failed.", vm.Name); }
            }
            if (lease is not null)
            {
                try
                {
                    await egress.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
                    TemplateCoordinator.DeleteRunNetDir(net.Value, taskId, _log);
                }
                catch (Exception ex) { _log.LogError(ex, "Releasing the network lease of verification boot {TaskId} failed.", taskId); }
            }
        }
    }
}

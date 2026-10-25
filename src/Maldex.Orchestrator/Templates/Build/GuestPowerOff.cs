using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Text;

namespace Maldex.Orchestrator.Templates.Build;

/// <summary>
/// Powers a template guest off through its agent and waits until the hypervisor says it has stopped. Shared by sealing (a disk
/// that may still be written to is never sealed) and building (provisioning ends with a clean shutdown).
/// </summary>
internal static class GuestPowerOff
{
    /// <summary>
    /// Sends <c>power_off</c> and waits for the guest to stop running, all within one <see cref="TemplateJobOptions.PowerOffTimeout"/>
    /// deadline. A refusal fails; a disconnect counts as acknowledged (whether the guest stopped is the hypervisor's word).
    /// </summary>
    /// <param name="fail">Builds the caller's error from a message and its cause, if any.</param>
    /// <returns>True when the hypervisor reports the guest not running in time; false when it is still running at the deadline.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static async Task<bool> PowerOffAsync(GuestSession session, IAgentChannel ch, IHypervisor hv, VmHandle vm, TemplateJobOptions o,
        TimeProvider time, Func<string, Exception?, Exception> fail, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(o.PowerOffTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        await AwaitPowerOffAckAsync(ch, session, fail, linked.Token).ConfigureAwait(false);
        var stopped = await WaitUntilStoppedAsync(hv, vm, o, time, fail, linked.Token, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return stopped;
    }

    /// <summary>
    /// Sends power_off and waits for its result. A refusal fails, also one that arrives exactly at the deadline. A disconnect (the
    /// OS went away before or while answering) counts as acknowledged, and so does running out of time: whether the guest really
    /// stopped is the hypervisor's word, checked next.
    /// </summary>
    private static async Task AwaitPowerOffAckAsync(IAgentChannel ch, GuestSession session, Func<string, Exception?, Exception> fail, CancellationToken ct)
    {
        var command = new PowerOffCommand(NewId());
        using var lease = session.Inbox.Open();
        string? refusal = null;
        try
        {
            await ch.SendAsync(command, ct).ConfigureAwait(false);
            while (true)
            {
                // What has already arrived is read before the deadline is looked at, so an answer that came in with it still counts.
                if (!lease.Reader.TryRead(out var message)) message = await lease.Reader.ReadAsync(ct).ConfigureAwait(false);
                if (message is not CommandResultMessage result || result.CommandId != command.CommandId) continue;
                if (!result.Success) refusal = GuestText.Clean(result.Error, 200);
                break;
            }
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException or IOException or ObjectDisposedException || ct.IsCancellationRequested)
        {
            // Disconnected, or out of time: the running-wait decides.
        }
        if (refusal is not null) throw fail($"powering off failed: the agent refused it ({refusal})", null);
    }

    /// <summary>
    /// True once the hypervisor says the guest is not running; false when the deadline passes first. <paramref name="ct"/> is the
    /// deadline linked to the caller's token; <paramref name="callerCt"/> is the caller's alone (the last look honours it too).
    /// </summary>
    private static async Task<bool> WaitUntilStoppedAsync(IHypervisor hv, VmHandle vm, TemplateJobOptions o, TimeProvider time,
        Func<string, Exception?, Exception> fail, CancellationToken ct, CancellationToken callerCt)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await hv.IsRunningAsync(vm, ct).ConfigureAwait(false)) return true;
                await Task.Delay(o.PollInterval, time, ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break; // whatever the error, the deadline or the caller ended the wait
            }
            catch (Exception ex)
            {
                throw fail($"could not check whether the guest stopped: {GuestText.Clean(ex.Message, 200)}", ex);
            }
        }

        // Out of time: one short last look, so a guest that stopped just now is not reported as dirty.
        using var lastTimeout = new CancellationTokenSource(o.PollInterval * 5, time);
        using var last = CancellationTokenSource.CreateLinkedTokenSource(callerCt, lastTimeout.Token);
        try
        {
            return !await hv.IsRunningAsync(vm, last.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!callerCt.IsCancellationRequested && lastTimeout.IsCancellationRequested)
        {
            return false; // the last look itself ran out of time: it did not power off
        }
        catch (Exception ex) when (!callerCt.IsCancellationRequested)
        {
            throw fail($"could not check whether the guest stopped: {GuestText.Clean(ex.Message, 200)}", ex);
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}

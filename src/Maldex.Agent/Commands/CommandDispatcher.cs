using Maldex.Core.Agent;

namespace Maldex.Agent.Commands;

/// <summary>
/// Executes host commands and turns every outcome into a <see cref="CommandResultMessage"/>; never throws
/// (except for cancellation). Extend by adding a case to the switch.
/// </summary>
public sealed class CommandDispatcher(ISampleOpener sample, IUrlOpener url, Action requestStop)
{
    private const int MaxErrorChars = 512;

    /// <summary>Called by the host once the result for <paramref name="command"/> has been written.</summary>
    public void AfterResultSent(AgentCommand command)
    {
        if (command is ShutdownCommand) requestStop();
    }

    public async Task<CommandResultMessage> DispatchAsync(AgentCommand command, CancellationToken ct)
    {
        try
        {
            switch (command)
            {
                case OpenUrlCommand c:
                    await url.OpenAsync(c.Url, ct).ConfigureAwait(false);
                    return new CommandResultMessage(c.CommandId, true);
                case ShutdownCommand c:
                    return new CommandResultMessage(c.CommandId, true);
                default:
                    // open_sample, put_file_chunk, collect_file and self_test arrive with later tasks.
                    _ = sample;
                    return new CommandResultMessage(command.CommandId, false, "unsupported command");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Length > MaxErrorChars ? ex.Message[..MaxErrorChars] : ex.Message;
            return new CommandResultMessage(command.CommandId, false, msg);
        }
    }
}

using Maldex.Agent.Probes;
using Maldex.Agent.Transfers;
using Maldex.Core.Agent;

namespace Maldex.Agent.Commands;

/// <summary>
/// Executes host commands and turns every outcome into a <see cref="CommandResultMessage"/>; never throws
/// (except for cancellation). Extend by adding a case to the switch.
/// </summary>
public sealed class CommandDispatcher : IDisposable
{
    private const int MaxErrorChars = 512;

    private readonly ISampleOpener _sample;
    private readonly IUrlOpener _url;
    private readonly Action _requestStop;
    private readonly string _desktop;
    private readonly InboundTransfers _inbound;
    private readonly TimeProvider _time;
    private readonly ISelfTestProbes _probes;

    /// <param name="desktopDirectory">Where delivered samples are placed; defaults to %USERPROFILE%\Desktop.</param>
    /// <param name="stagingDirectory">Where in-flight chunks are buffered; defaults to a folder under the temp directory.</param>
    public CommandDispatcher(ISampleOpener sample, IUrlOpener url, Action requestStop, string? desktopDirectory = null, string? stagingDirectory = null, TimeProvider? time = null, ISelfTestProbes? probes = null)
    {
        _sample = sample;
        _url = url;
        _requestStop = requestStop;
        _desktop = desktopDirectory ?? Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        _inbound = new InboundTransfers(stagingDirectory ?? Path.Combine(Path.GetTempPath(), "maldex-inbound"));
        _time = time ?? TimeProvider.System;
        _probes = probes ?? new SelfTestProbes();
    }

    /// <summary>Called by the host once the result for <paramref name="command"/> has been written.</summary>
    public void AfterResultSent(AgentCommand command)
    {
        if (command is ShutdownCommand) _requestStop();
    }

    /// <param name="emit">Writes a message to the host before the command's own result (used by <c>collect_file</c>).</param>
    public async Task<CommandResultMessage> DispatchAsync(AgentCommand command, CancellationToken ct, Func<AgentMessage, CancellationToken, Task>? emit = null)
    {
        try
        {
            switch (command)
            {
                case OpenUrlCommand c:
                    await _url.OpenAsync(c.Url, ct).ConfigureAwait(false);
                    return new CommandResultMessage(c.CommandId, true);
                case PutFileChunkCommand c:
                    _inbound.AddChunk(c);
                    return new CommandResultMessage(c.CommandId, true);
                case OpenSampleCommand c:
                    var path = _inbound.Publish(c.BlobKey, c.FileName, _desktop);
                    await _sample.OpenAsync(path, ct).ConfigureAwait(false);
                    return new CommandResultMessage(c.CommandId, true);
                case CollectFileCommand c:
                    if (emit is null) return new CommandResultMessage(c.CommandId, false, "collect_file is not available here");
                    await OutboundFileSender.SendAsync(c.CommandId, c.Path, emit, _time, ct).ConfigureAwait(false);
                    return new CommandResultMessage(c.CommandId, true);
                case ShutdownCommand c:
                    return new CommandResultMessage(c.CommandId, true);
                case SelfTestCommand c:
                    if (emit is null) return new CommandResultMessage(c.CommandId, false, "self_test is not available here");
                    await emit(await _probes.RunAsync(c, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    return new CommandResultMessage(c.CommandId, true);
                default:
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

    public void Dispose() => _inbound.Dispose();
}

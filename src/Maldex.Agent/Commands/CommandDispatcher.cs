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
    private readonly IProvenanceProbe? _provenance;
    private readonly IComponentInstaller? _components;
    private readonly IPowerController? _power;
    private readonly string _componentsDirectory;

    /// <param name="desktopDirectory">Where delivered samples are placed; defaults to %USERPROFILE%\Desktop.</param>
    /// <param name="stagingDirectory">Where in-flight chunks are buffered; defaults to a folder under the temp directory.</param>
    public CommandDispatcher(ISampleOpener sample, IUrlOpener url, Action requestStop, string? desktopDirectory = null, string? stagingDirectory = null, TimeProvider? time = null, ISelfTestProbes? probes = null, IProvenanceProbe? provenance = null,
        IComponentInstaller? components = null, IPowerController? power = null)
    {
        _sample = sample;
        _url = url;
        _requestStop = requestStop;
        _desktop = desktopDirectory ?? Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        var staging = stagingDirectory ?? Path.Combine(Path.GetTempPath(), "maldex-inbound");
        _inbound = new InboundTransfers(staging);
        _componentsDirectory = Path.Combine(staging, "components");
        _time = time ?? TimeProvider.System;
        _probes = probes ?? new SelfTestProbes();
        _provenance = provenance;
        _components = components;
        _power = power;
    }

    /// <summary>Called by the host once the result for <paramref name="command"/> has been written.</summary>
    public void AfterResultSent(AgentCommand command)
    {
        if (command is ShutdownCommand) _requestStop();
        else if (command is PowerOffCommand) PowerOff();
    }

    private void PowerOff()
    {
        try { _power?.PowerOff(); }
        catch (Exception ex)
        {
            // The result is already written; the host sees the guest never stopping and times out.
            try { AgentLog.Write("power_off failed: " + ex.Message); } catch { }
        }
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
                case ProvenanceCommand c:
                    if (emit is null || _provenance is null) return new CommandResultMessage(c.CommandId, false, "provenance is not available here");
                    var report = _provenance.Probe();
                    await emit(new ProvenanceMessage(c.CommandId, report.WindowsEdition, report.WindowsBuild, AgentHost.BuildId,
                        report.OfficePresent, report.RealTimeProtectionEnabled), ct).ConfigureAwait(false);
                    return new CommandResultMessage(c.CommandId, true);
                case ApplyComponentsCommand c:
                    return await ApplyComponentsAsync(c, ct).ConfigureAwait(false);
                case PowerOffCommand c:
                    return _power is null
                        ? new CommandResultMessage(c.CommandId, false, "power_off is not available here")
                        : new CommandResultMessage(c.CommandId, true); // the OS shutdown starts in AfterResultSent
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

    /// <summary>
    /// Runs the <c>apply_components</c> steps in order; the first failure ends the command and names its step. Every id must be a
    /// component transfer (<see cref="AgentLimits.ComponentTransferPrefix"/>) so a sample can never be installed as a component.
    /// </summary>
    private async Task<CommandResultMessage> ApplyComponentsAsync(ApplyComponentsCommand c, CancellationToken ct)
    {
        if (_components is null) return new CommandResultMessage(c.CommandId, false, "apply_components is not available here");
        foreach (var id in new[] { c.CaCertTransferId, c.SysmonConfigTransferId, c.AgentTransferId })
        {
            if (!AgentLimits.IsComponentTransfer(id))
                return new CommandResultMessage(c.CommandId, false, AgentLimits.NotComponentTransferMessage(id));
        }

        try
        {
            string ca, sysmon, agent;
            try
            {
                ca = _inbound.Publish(c.CaCertTransferId, "maldex-ca.cer", _componentsDirectory);
                sysmon = _inbound.Publish(c.SysmonConfigTransferId, "sysmon-config.xml", _componentsDirectory);
                agent = _inbound.Publish(c.AgentTransferId, "Maldex.Agent.exe", _componentsDirectory);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Failed(c.CommandId, "publish_components", ex);
            }

            var steps = new (string Name, Action Run)[]
            {
                ("import_ca", () => _components.ImportCaCertificate(ca)),
                ("sysmon_config", () => _components.ApplySysmonConfig(sysmon)),
                ("swap_agent", () => _components.SwapAgent(agent)),
                ("clear_logs", _components.ClearLogsAndTemp),
            };
            foreach (var (name, run) in steps)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await Task.Run(run, CancellationToken.None).ConfigureAwait(false); // the steps block on processes and the file system
                }
                catch (Exception ex)
                {
                    return Failed(c.CommandId, name, ex);
                }
            }
            return new CommandResultMessage(c.CommandId, true);
        }
        finally
        {
            try { if (Directory.Exists(_componentsDirectory)) Directory.Delete(_componentsDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static CommandResultMessage Failed(string commandId, string step, Exception ex)
    {
        var msg = $"{step} failed: {ex.Message}";
        return new CommandResultMessage(commandId, false, msg.Length > MaxErrorChars ? msg[..MaxErrorChars] : msg);
    }

    public void Dispose() => _inbound.Dispose();
}

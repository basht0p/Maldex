using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Core.Live;
using Maldex.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maldex.Orchestrator;

/// <summary>
/// Drives one analysis run from <c>Preparing</c> to <c>Processing</c> (or <c>Failed</c>): egress, VM, agent,
/// live ingestion, then teardown. Every run gets its own DI scope, so concurrent runs never share a DbContext.
/// </summary>
public sealed class RunCoordinator(
    IServiceScopeFactory scopes,
    IHypervisor hypervisor,
    IAgentChannelFactory channels,
    IEgressRouter egress,
    ITaskEventPublisher publisher,
    IArtifactStore artifacts,
    IOptions<OrchestratorOptions> options,
    TimeProvider time,
    ILogger<RunCoordinator> logger)
{
    private const int MaxViolations = 10;
    private const int MaxTransitionAttempts = 5;

    private readonly OrchestratorOptions _options = options.Value;

    /// <summary>
    /// Runs one task that the worker has already moved to <c>Preparing</c>. Returns once the task is
    /// <c>Processing</c>, <c>Failed</c> or <c>Cancelled</c>. The VM and network lease are always released.
    /// </summary>
    public async Task RunAsync(Guid taskId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MaldexDbContext>();

        var task = await db.Tasks.SingleAsync(t => t.Id == taskId, ct).ConfigureAwait(false);
        if (task.Status != RunStatus.Preparing)
        {
            logger.LogWarning("Task {TaskId} is {Status}, not Preparing; nothing to run.", taskId, task.Status);
            return;
        }

        NetworkLease? lease = null;
        VmHandle? vm = null;
        IAgentChannel? channel = null;
        try
        {
            try
            {
                lease = await egress.CreateAsync(task.Id, task.NetworkMode, task.ExitCountry, ct).ConfigureAwait(false);
            }
            catch (EgressUnavailableException ex)
            {
                await FailAsync(db, task, $"egress unavailable: {ex.Message}").ConfigureAwait(false);
                return;
            }

            var command = await BuildOpenCommandAsync(db, task, ct).ConfigureAwait(false);
            if (command is null)
            {
                await FailAsync(db, task, "task has neither a sample nor a url").ConfigureAwait(false);
                return;
            }

            vm = await hypervisor.StartAsync(
                new VmSpec(task.Id, task.Image, _options.VmMemoryMiB, _options.VmVCpus, lease.NetworkId), ct).ConfigureAwait(false);
            channel = await channels.ConnectAsync(vm, ct).ConfigureAwait(false);

            await RunGuestAsync(db, task, channel, command, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host shutdown: the reconciler picks the task up on restart
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run {TaskId} failed.", taskId);
            await FailAsync(db, task, $"run failed: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            await ReleaseAsync(taskId, channel, vm, lease).ConfigureAwait(false);
        }

        if (task.Status == RunStatus.Collecting)
            await TransitionAsync(db, task, RunStatus.Processing, "collection complete", null, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Waits for the agent, then ingests until the run ends and moves the task to Collecting (or Failed if the agent never became ready).</summary>
    private async Task RunGuestAsync(MaldexDbContext db, AnalysisTask task, IAgentChannel channel, AgentCommand command, CancellationToken ct)
    {
        using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var enumerator = channel.ReadAsync(readerCts.Token).GetAsyncEnumerator(readerCts.Token);
        var disposed = false;
        try
        {
            if (!await WaitForReadyAsync(enumerator, readerCts, ct).ConfigureAwait(false))
            {
                await FailAsync(db, task, $"agent not ready after {_options.AgentReadyTimeout.TotalSeconds:0} s").ConfigureAwait(false);
                return;
            }

            var now = time.GetUtcNow();
            await TransitionAsync(db, task, RunStatus.Running, "agent ready", t =>
            {
                t.StartedAt = now;
                t.DeadlineAt = now.AddSeconds(t.DurationSec);
            }, ct).ConfigureAwait(false);

            await channel.SendAsync(command, ct).ConfigureAwait(false);

            var ingestor = new EventIngestor(task.Id, db, artifacts, publisher, time, logger);
            var messages = Channel.CreateBounded<AgentMessage>(new BoundedChannelOptions(1024)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

            var reader = ReadLoopAsync(enumerator, messages.Writer, readerCts.Token);
            var flusher = FlushLoopAsync(messages.Reader, ingestor, ct);
            disposed = true; // the reader task now owns the enumerator

            string reason;
            try
            {
                reason = await ControlLoopAsync(task.Id, reader, flusher, channel, ct).ConfigureAwait(false);
            }
            catch
            {
                // Never leave the flusher touching the DbContext while the caller records the failure.
                await readerCts.CancelAsync().ConfigureAwait(false);
                await reader.ConfigureAwait(false);
                try { await flusher.ConfigureAwait(false); } catch (Exception) { /* the original error wins */ }
                throw;
            }

            await readerCts.CancelAsync().ConfigureAwait(false);
            await reader.ConfigureAwait(false);
            await flusher.ConfigureAwait(false); // drains whatever the reader queued, then flushes it

            var ended = time.GetUtcNow();
            await TransitionAsync(db, task, RunStatus.Collecting, reason, t => t.EndedAt = ended, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!disposed) await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Waits for the agent's ReadyMessage. Returns false on timeout or if the channel closes first.</summary>
    private async Task<bool> WaitForReadyAsync(IAsyncEnumerator<AgentMessage> enumerator, CancellationTokenSource readerCts, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + _options.AgentReadyTimeout;
        while (true)
        {
            var remaining = deadline - time.GetUtcNow();
            var next = enumerator.MoveNextAsync().AsTask();
            try
            {
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                if (!await next.WaitAsync(remaining, time, ct).ConfigureAwait(false)) return false;
            }
            catch (TimeoutException)
            {
                await readerCts.CancelAsync().ConfigureAwait(false);
                try { await next.ConfigureAwait(false); } catch (OperationCanceledException) { }
                return false;
            }

            if (enumerator.Current is ReadyMessage) return true;
            // Anything the agent sends before it is ready is not telemetry yet; ignore it.
        }
    }

    private static async Task ReadLoopAsync(IAsyncEnumerator<AgentMessage> enumerator, ChannelWriter<AgentMessage> writer, CancellationToken stop)
    {
        Exception? error = null;
        try
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                await writer.WriteAsync(enumerator.Current, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        finally
        {
            writer.TryComplete(error);
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task FlushLoopAsync(ChannelReader<AgentMessage> reader, EventIngestor ingestor, CancellationToken ct)
    {
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            var taken = 0;
            while (taken < _options.FlushBatchSize && reader.TryRead(out var m))
            {
                ingestor.Add(m);
                taken++;
            }

            await ingestor.FlushAsync(ct).ConfigureAwait(false);

            // Pace flushes so bursts batch up, but wake immediately once the reader is done.
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pause = Task.Delay(_options.FlushInterval, time, wake.Token);
            await Task.WhenAny(pause, reader.Completion).ConfigureAwait(false);
            await wake.CancelAsync().ConfigureAwait(false);
            try { await pause.ConfigureAwait(false); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }

        await reader.Completion.ConfigureAwait(false); // surface a read-side failure
    }

    /// <summary>Returns why ingestion ended. Re-reads StopRequestedAt and DeadlineAt every poll interval.</summary>
    private async Task<string> ControlLoopAsync(Guid taskId, Task reader, Task flusher, IAgentChannel channel, CancellationToken ct)
    {
        // A second scope: the run's own DbContext is busy flushing on another thread.
        await using var controlScope = scopes.CreateAsyncScope();
        var control = controlScope.ServiceProvider.GetRequiredService<MaldexDbContext>();

        while (true)
        {
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var poll = Task.Delay(_options.ControlPollInterval, time, wake.Token);
            await Task.WhenAny(poll, reader, flusher).ConfigureAwait(false);
            await wake.CancelAsync().ConfigureAwait(false);
            try { await poll.ConfigureAwait(false); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            ct.ThrowIfCancellationRequested();

            if (reader.IsCompleted)
            {
                await reader.ConfigureAwait(false);
                return channel.Violations >= MaxViolations ? "agent protocol violations" : "agent disconnected";
            }

            if (flusher.IsFaulted) await flusher.ConfigureAwait(false); // surface an ingestion failure now

            var state = await control.Tasks.AsNoTracking()
                .Where(t => t.Id == taskId)
                .Select(t => new { t.StopRequestedAt, t.DeadlineAt })
                .SingleAsync(ct).ConfigureAwait(false);

            if (state.StopRequestedAt is not null) return "stopped by analyst";
            if (state.DeadlineAt is { } deadline && time.GetUtcNow() >= deadline) return "deadline reached";
        }
    }

    private static async Task<AgentCommand?> BuildOpenCommandAsync(MaldexDbContext db, AnalysisTask task, CancellationToken ct)
    {
        var commandId = Guid.NewGuid().ToString("N");
        if (!string.IsNullOrEmpty(task.Url)) return new OpenUrlCommand(commandId, task.Url);
        if (task.SampleSha256 is null) return null;

        var blobKey = await db.Samples.AsNoTracking()
            .Where(s => s.Sha256 == task.SampleSha256)
            .Select(s => s.BlobKey)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return blobKey is null ? null : new OpenSampleCommand(commandId, task.OriginalFileName ?? "sample", blobKey);
    }

    private async Task ReleaseAsync(Guid taskId, IAgentChannel? channel, VmHandle? vm, NetworkLease? lease)
    {
        if (channel is not null)
        {
            try { await channel.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { logger.LogWarning(ex, "Closing agent channel for {TaskId} failed.", taskId); }
        }

        if (vm is not null)
        {
            try { await hypervisor.DestroyAsync(vm, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { logger.LogError(ex, "Destroying VM {Vm} for {TaskId} failed.", vm.Name, taskId); }
        }

        if (lease is not null)
        {
            try { await egress.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { logger.LogError(ex, "Releasing network lease for {TaskId} failed.", taskId); }
        }
    }

    private async Task FailAsync(MaldexDbContext db, AnalysisTask task, string reason)
    {
        try
        {
            var ended = time.GetUtcNow();
            await TransitionAsync(db, task, RunStatus.Failed, reason, t =>
            {
                t.EndedAt ??= ended;
                t.FailureReason = reason;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record failure of task {TaskId}: {Reason}", task.Id, reason);
        }
    }

    /// <summary>
    /// Applies one state-machine transition and saves it. The task row is also written by the API (stop and
    /// extend requests), so it is reloaded first and the save retried on a concurrency conflict.
    /// </summary>
    private async Task TransitionAsync(MaldexDbContext db, AnalysisTask task, RunStatus to, string reason, Action<AnalysisTask>? mutate, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await db.Entry(task).ReloadAsync(ct).ConfigureAwait(false);
            task.TransitionTo(to, reason, time.GetUtcNow());
            mutate?.Invoke(task);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxTransitionAttempts)
            {
                foreach (var h in task.StatusHistory.Where(h => db.Entry(h).State == EntityState.Added).ToList())
                {
                    db.Entry(h).State = EntityState.Detached;
                    task.StatusHistory.Remove(h);
                }
            }
        }

        try
        {
            await publisher.PublishStatusAsync(new TaskStatusUpdate(task.Id, task.Status, task.Verdict, task.DeadlineAt, reason), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Publishing status {Status} for task {TaskId} failed.", task.Status, task.Id);
        }
    }
}

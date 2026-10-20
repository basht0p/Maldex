using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Core.Live;
using Maldex.Core.Text;
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
    private const int MaxAttempts = 2;
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
        try
        {
            try
            {
                lease = await egress.CreateAsync(task.Id, task.NetworkMode, task.ExitCountry, ct).ConfigureAwait(false);
            }
            catch (EgressUnavailableException ex)
            {
                // Fail closed: no VM, no retry, no fallback to another network mode.
                await FailAsync(db, task, $"egress unavailable: {ex.Message}").ConfigureAwait(false);
                return;
            }

            var command = await BuildOpenCommandAsync(db, task, ct).ConfigureAwait(false);
            if (command is null)
            {
                await FailAsync(db, task, "task has neither a sample nor a url").ConfigureAwait(false);
                return;
            }

            // Retry once on a fresh VM (same network lease), then Failed. Each attempt releases its own VM.
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                await SetAttemptAsync(db, task, attempt, ct).ConfigureAwait(false);
                var outcome = await AttemptAsync(db, task, lease, command, ct).ConfigureAwait(false);
                if (outcome == AttemptOutcome.Completed) break;

                if (outcome == AttemptOutcome.Stopped)
                {
                    await CancelAsync(db, task, "stopped by analyst").ConfigureAwait(false);
                    return;
                }

                if (attempt == MaxAttempts)
                {
                    var reason = outcome == AttemptOutcome.VmStartFailed
                        ? $"vm failed to start after {MaxAttempts} attempts"
                        : $"agent not ready after {MaxAttempts} attempts";
                    await FailAsync(db, task, reason).ConfigureAwait(false);
                    return;
                }

                logger.LogWarning("Run {TaskId} attempt {Attempt} ended with {Outcome}; retrying on a fresh VM.", taskId, attempt, outcome);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host shutdown: the reconciler picks the task up on restart
        }
        catch (SampleDeliveryException ex)
        {
            // Not retried: a second VM would receive the same sample the same way.
            logger.LogWarning("Run {TaskId}: {Reason}", taskId, ex.Message);
            await FailAsync(db, task, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run {TaskId} failed.", taskId);
            await FailAsync(db, task, $"run failed: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            await ReleaseLeaseAsync(taskId, lease).ConfigureAwait(false);
        }

        if (task.Status == RunStatus.Collecting)
            await TransitionAsync(db, task, RunStatus.Processing, "collection complete", null, CancellationToken.None).ConfigureAwait(false);
    }

    private enum AttemptOutcome { Completed, VmStartFailed, AgentNotReady, Stopped }

    /// <summary>One try on a fresh VM. The VM and agent channel are always released before this returns.</summary>
    private async Task<AttemptOutcome> AttemptAsync(MaldexDbContext db, AnalysisTask task, NetworkLease lease, AgentCommand command, CancellationToken ct)
    {
        VmHandle? vm = null;
        IAgentChannel? channel = null;
        try
        {
            try
            {
                vm = await hypervisor.StartAsync(
                    new VmSpec(task.Id, task.Image, _options.VmMemoryMiB, _options.VmVCpus, lease.NetworkId), ct).ConfigureAwait(false);
            }
            catch (VmStartException ex)
            {
                logger.LogWarning(ex, "VM start failed for {TaskId} (attempt {Attempt}).", task.Id, task.Attempt);
                return AttemptOutcome.VmStartFailed;
            }

            channel = await channels.ConnectAsync(vm, ct).ConfigureAwait(false);
            return await RunGuestAsync(db, task, channel, command, ct).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseGuestAsync(task.Id, channel, vm).ConfigureAwait(false);
        }
    }

    /// <summary>Records the attempt number. The row is also written by the API, so reload and retry on a conflict.</summary>
    private async Task SetAttemptAsync(MaldexDbContext db, AnalysisTask task, int attempt, CancellationToken ct)
    {
        for (var tries = 1; ; tries++)
        {
            await db.Entry(task).ReloadAsync(ct).ConfigureAwait(false);
            task.Attempt = attempt;
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException) when (tries < MaxTransitionAttempts) { }
        }
    }

    /// <summary>
    /// Waits for the agent, then ingests until the run ends and moves the task to Collecting.
    /// Returns <c>AgentNotReady</c> (without touching the task) if the agent never became ready, or <c>Stopped</c>
    /// if the analyst asked to stop while the VM was still preparing.
    /// </summary>
    private async Task<AttemptOutcome> RunGuestAsync(MaldexDbContext db, AnalysisTask task, IAgentChannel channel, AgentCommand command, CancellationToken ct)
    {
        using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var enumerator = channel.ReadAsync(readerCts.Token).GetAsyncEnumerator(readerCts.Token);
        var disposed = false;
        try
        {
            var ready = await WaitForReadyAsync(db, task.Id, enumerator, readerCts, ct).ConfigureAwait(false);
            if (ready != ReadyResult.Ready)
                return ready == ReadyResult.Stopped ? AttemptOutcome.Stopped : AttemptOutcome.AgentNotReady;

            // The ingestor has its own DbContext: the run's context stays free for status transitions while telemetry
            // is flushed, which now happens from the moment the agent is ready (delivery and collection run alongside it).
            await using var ingestScope = scopes.CreateAsyncScope();
            var ingestor = new EventIngestor(task.Id, ingestScope.ServiceProvider.GetRequiredService<MaldexDbContext>(), artifacts, publisher, time, logger);
            var messages = Channel.CreateBounded<AgentMessage>(new BoundedChannelOptions(1024)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

            var inbox = new TransferInbox();
            var reader = ReadLoopAsync(enumerator, messages.Writer, inbox, readerCts.Token);
            var flusher = FlushLoopAsync(messages.Reader, ingestor, ct);
            disposed = true; // the reader task now owns the enumerator

            IReadOnlyList<DroppedFile> dropped = [];
            try
            {
                // SEAM (Task 13): the isolation self-test runs here, after the agent is ready and before anything is delivered.

                if (command is OpenSampleCommand open)
                    await DeliverSampleAsync(db, channel, inbox, task, open, ct).ConfigureAwait(false);

                var now = time.GetUtcNow();
                await TransitionAsync(db, task, RunStatus.Running, "agent ready", t =>
                {
                    t.StartedAt = now;
                    t.DeadlineAt = now.AddSeconds(t.DurationSec);
                }, ct).ConfigureAwait(false);

                // A sample is opened by its delivery; a URL is opened now.
                if (command is not OpenSampleCommand) await channel.SendAsync(command, ct).ConfigureAwait(false);

                var reason = await ControlLoopAsync(task.Id, reader, flusher, channel, ct).ConfigureAwait(false);

                var ended = time.GetUtcNow();
                await TransitionAsync(db, task, RunStatus.Collecting, reason, t => t.EndedAt = ended, ct).ConfigureAwait(false);

                // Telemetry keeps flowing while files are pulled out, as long as the guest is still there.
                if (!reader.IsCompleted) dropped = await CollectDroppedAsync(channel, inbox, task, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never leave the reader or flusher running while the caller records the failure.
                await readerCts.CancelAsync().ConfigureAwait(false);
                await reader.ConfigureAwait(false);
                try { await flusher.ConfigureAwait(false); } catch (Exception) { /* the original error wins */ }
                // A stop during delivery ends like a stop during the ready wait: Cancelled from Preparing.
                if (ex is DeliveryStoppedException) return AttemptOutcome.Stopped;
                throw;
            }

            await readerCts.CancelAsync().ConfigureAwait(false);
            await reader.ConfigureAwait(false);
            await flusher.ConfigureAwait(false); // drains whatever the reader queued, then flushes it

            await SaveDroppedArtifactsAsync(db, task, dropped, ct).ConfigureAwait(false);
            return AttemptOutcome.Completed;
        }
        finally
        {
            if (!disposed) await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Streams the stored sample into the guest and has it opened. Any failure fails the run in Preparing (no retry).</summary>
    private async Task DeliverSampleAsync(MaldexDbContext db, IAgentChannel channel, TransferInbox inbox, AnalysisTask task, OpenSampleCommand open, CancellationToken ct)
    {
        Stream blob;
        try
        {
            blob = await artifacts.OpenReadAsync(open.BlobKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(ex, "Reading the stored sample of task {TaskId} failed.", task.Id);
            throw new SampleDeliveryException("the stored sample could not be read");
        }

        await using (blob.ConfigureAwait(false))
        {
            if (!blob.CanSeek || task.SampleSha256 is null) throw new SampleDeliveryException("the stored sample cannot be sized");
            await new SampleDelivery(time, pollInterval: _options.ControlPollInterval)
                .DeliverAsync(channel, inbox, open.BlobKey, open.FileName, blob, blob.Length, task.SampleSha256, ct, async token =>
                    await db.Tasks.AsNoTracking().Where(t => t.Id == task.Id).Select(t => t.StopRequestedAt).SingleAsync(token).ConfigureAwait(false) is not null)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Pulls the files the guest created out of it. Never throws for a bad or missing file; the run goes on.</summary>
    private async Task<IReadOnlyList<DroppedFile>> CollectDroppedAsync(IAgentChannel channel, TransferInbox inbox, AnalysisTask task, CancellationToken ct)
    {
        try
        {
            return await new DroppedFileCollector(artifacts, time, logger)
                .CollectAsync(channel, inbox, inbox.CreatedPaths, task.SampleSha256, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Collecting dropped files of task {TaskId} failed; continuing without them.", task.Id);
            return [];
        }
    }

    private async Task SaveDroppedArtifactsAsync(MaldexDbContext db, AnalysisTask task, IReadOnlyList<DroppedFile> dropped, CancellationToken ct)
    {
        if (dropped.Count == 0) return;
        var now = time.GetUtcNow();
        foreach (var f in dropped)
        {
            db.Artifacts.Add(new Artifact
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                Kind = ArtifactKind.Dropped,
                BlobKey = f.Blob.Key,
                Sha256 = f.Blob.Sha256,
                Size = f.Blob.Size,
                // Artifact.GuestPath is nvarchar(1024); Clean adds a 12-character marker when it cuts.
                GuestPath = GuestText.Clean(f.GuestPath, 1024 - 12),
                CreatedAt = now,
            });
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private enum ReadyResult { Ready, NotReady, Stopped }

    /// <summary>
    /// Waits for the agent's ReadyMessage, re-reading StopRequestedAt every poll interval. NotReady on timeout or if the
    /// channel closes first. On every exit the pending read has finished, so the caller may dispose the enumerator.
    /// </summary>
    private async Task<ReadyResult> WaitForReadyAsync(MaldexDbContext db, Guid taskId, IAsyncEnumerator<AgentMessage> enumerator, CancellationTokenSource readerCts, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + _options.AgentReadyTimeout;
        var nextPollAt = time.GetUtcNow() + _options.ControlPollInterval;
        Task<bool>? next = null;
        try
        {
            while (true)
            {
                next ??= enumerator.MoveNextAsync().AsTask();
                var now = time.GetUtcNow();
                if (deadline - now <= TimeSpan.Zero) return await AbandonAsync(ReadyResult.NotReady).ConfigureAwait(false);

                var wait = Min(deadline - now, nextPollAt - now);
                if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                try
                {
                    if (await next.WaitAsync(wait, time, ct).ConfigureAwait(false))
                    {
                        next = null;
                        if (enumerator.Current is ReadyMessage) return ReadyResult.Ready;
                        // Anything the agent sends before it is ready is not telemetry yet; ignore it.
                    }
                    else
                    {
                        next = null;
                        return ReadyResult.NotReady; // the channel closed first
                    }
                }
                catch (TimeoutException)
                {
                    // the same MoveNextAsync is still in flight
                }

                // Poll on the clock, not only when the guest is quiet: a chatty guest must not defer a stop.
                if (time.GetUtcNow() >= nextPollAt)
                {
                    nextPollAt = time.GetUtcNow() + _options.ControlPollInterval;
                    var stop = await db.Tasks.AsNoTracking()
                        .Where(t => t.Id == taskId)
                        .Select(t => t.StopRequestedAt)
                        .SingleAsync(ct).ConfigureAwait(false);
                    if (stop is not null) return await AbandonAsync(ReadyResult.Stopped).ConfigureAwait(false);
                }
            }
        }
        catch when (next is { IsCompleted: false })
        {
            // Cancellation or a failed query: let the in-flight read finish before the enumerator can be disposed.
            await readerCts.CancelAsync().ConfigureAwait(false);
            try { await next.ConfigureAwait(false); } catch (Exception) { /* the original error wins */ }
            throw;
        }

        static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

        async Task<ReadyResult> AbandonAsync(ReadyResult result)
        {
            await readerCts.CancelAsync().ConfigureAwait(false);
            // The cancelled read may end in any exception; a stop or timeout must never turn into a failure.
            if (next is not null) { try { await next.ConfigureAwait(false); } catch (Exception) { } }
            return result;
        }
    }

    private static async Task ReadLoopAsync(IAsyncEnumerator<AgentMessage> enumerator, ChannelWriter<AgentMessage> writer, TransferInbox inbox, CancellationToken stop)
    {
        Exception? error = null;
        try
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                // Transfer replies go to whoever is waiting for them; everything else is telemetry.
                if (await inbox.OfferAsync(enumerator.Current, stop).ConfigureAwait(false)) continue;
                await writer.WriteAsync(enumerator.Current, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        finally
        {
            inbox.Complete();
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

    private async Task ReleaseGuestAsync(Guid taskId, IAgentChannel? channel, VmHandle? vm)
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
    }

    private async Task ReleaseLeaseAsync(Guid taskId, NetworkLease? lease)
    {
        if (lease is null) return;
        try { await egress.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { logger.LogError(ex, "Releasing network lease for {TaskId} failed.", taskId); }
    }

    private async Task CancelAsync(MaldexDbContext db, AnalysisTask task, string reason)
    {
        try
        {
            var ended = time.GetUtcNow();
            await TransitionAsync(db, task, RunStatus.Cancelled, reason, t => t.EndedAt ??= ended, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record cancellation of task {TaskId}: {Reason}", task.Id, reason);
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
        await TaskTransitions.TransitionWithRetryAsync(db, task, to, reason, time.GetUtcNow(), mutate, ct, maxAttempts: MaxTransitionAttempts).ConfigureAwait(false);

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

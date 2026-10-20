using System.Threading.Channels;
using Maldex.Core.Abstractions;
using Maldex.Core.Agent;
using Maldex.Core.Domain;
using Maldex.Core.Live;
using Maldex.Core.Text;
using Maldex.Data;
using Maldex.Infrastructure.Isolation;
using Maldex.Infrastructure.Net;
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
    ICaptureService captureService,
    IRunNetworkInfo networkInfo,
    IExpectedEgress expectedEgress,
    IOptions<NetOptions> netOptions,
    IOptions<SelfTestOptions> selfTestOptions,
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

    /// <summary>The run's capture, started after the isolation self-test and finished after the VM is gone.</summary>
    private sealed class CaptureSession
    {
        public ICaptureHandle? Handle { get; set; }
        public bool Used { get; set; }
        /// <summary>True when everything captured was stored, so the run's leftover network files can go.</summary>
        public bool Stored { get; set; } = true;
    }

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
        var capture = new CaptureSession();
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
                var outcome = await AttemptAsync(db, task, lease, command, capture, ct).ConfigureAwait(false);
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
        catch (IsolationException ex)
        {
            // Not retried: a second VM would sit on the same network.
            logger.LogWarning("Run {TaskId}: {Reason}", taskId, ex.Message);
            await FailAsync(db, task, ex.Message).ConfigureAwait(false);
        }
        catch (CaptureStartException ex)
        {
            // Not retried, and nothing was delivered: the sample never runs without a recording.
            logger.LogError(ex, "Run {TaskId}: capture failed to start.", taskId);
            await FailAsync(db, task, $"capture failed to start: {GuestText.Clean(ex.Message, 500)}").ConfigureAwait(false);
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
            // The run's net dir (lease record, daemon logs, the router's flows copy) goes once the lease is released, whether or
            // not a capture ran. It stays when the release failed (the reconciler needs the lease record) or when captured
            // data could not be stored (the router's copy is then the only one left).
            if (await ReleaseLeaseAsync(taskId, lease).ConfigureAwait(false) && (!capture.Used || capture.Stored)) CleanupRunNetDir(taskId);
        }

        if (task.Status == RunStatus.Collecting)
            await TransitionAsync(db, task, RunStatus.Processing, "collection complete", null, CancellationToken.None).ConfigureAwait(false);
    }

    private enum AttemptOutcome { Completed, VmStartFailed, AgentNotReady, Stopped }

    /// <summary>One try on a fresh VM. The VM and agent channel are always released before this returns.</summary>
    private async Task<AttemptOutcome> AttemptAsync(MaldexDbContext db, AnalysisTask task, NetworkLease lease, AgentCommand command, CaptureSession capture, CancellationToken ct)
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
            return await RunGuestAsync(db, task, lease, channel, command, capture, ct).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseGuestAsync(task.Id, channel, vm).ConfigureAwait(false);
            // The VM is gone, so no more traffic: stop the capture and keep what it recorded (before the lease is released).
            await FinishCaptureAsync(db, task, capture).ConfigureAwait(false);
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
    private async Task<AttemptOutcome> RunGuestAsync(MaldexDbContext db, AnalysisTask task, NetworkLease lease, IAgentChannel channel, AgentCommand command, CaptureSession capture, CancellationToken ct)
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

            var inbox = new TransferInbox((enumerator.Current as ReadyMessage)?.Pid);
            var reader = ReadLoopAsync(enumerator, messages.Writer, inbox, readerCts.Token);
            var flusher = FlushLoopAsync(messages.Reader, ingestor, ct);
            disposed = true; // the reader task now owns the enumerator

            IReadOnlyList<DroppedFile> dropped = [];
            try
            {
                // The isolation self-test runs after the agent is ready and before anything is delivered or opened: no sample reaches a guest that failed it.
                await RunSelfTestAsync(db, task, lease, channel, inbox, ct).ConfigureAwait(false);

                // Recording starts before anything is delivered; if it cannot start, the sample is never sent.
                await StartCaptureAsync(lease, capture, ct).ConfigureAwait(false);

                // The sample is delivered in Preparing (a failure or stop here never reaches Running)...
                if (command is OpenSampleCommand open)
                    await DeliverSampleAsync(db, channel, inbox, task, open, ct).ConfigureAwait(false);

                var now = time.GetUtcNow();
                await TransitionAsync(db, task, RunStatus.Running, "agent ready; isolation self-test passed", t =>
                {
                    t.StartedAt = now;
                    t.DeadlineAt = now.AddSeconds(t.DurationSec);
                }, ct).ConfigureAwait(false);

                // ...and opened only once the run is Running, so the live view sees it start. From here a failed open is a
                // run error (Running -> Failed), not a Preparing failure.
                inbox.MarkOpened();
                if (command is OpenSampleCommand sample)
                    await new SampleDelivery(time, pollInterval: _options.ControlPollInterval)
                        .OpenAsync(channel, inbox, sample.BlobKey, sample.FileName, ct).ConfigureAwait(false);
                else
                    await channel.SendAsync(command, ct).ConfigureAwait(false);

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
                if (ex is DeliveryStoppedException or SelfTestStoppedException) return AttemptOutcome.Stopped;
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

    private async Task StartCaptureAsync(NetworkLease lease, CaptureSession capture, CancellationToken ct)
    {
        try
        {
            capture.Handle = await captureService.StartAsync(lease, ct).ConfigureAwait(false);
            capture.Used = true;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or CaptureStartException))
        {
            throw new CaptureStartException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Stops the capture and stores its files as artifacts. Never throws: a problem becomes a processing warning on the task.
    /// Not cancellable, so a host shutdown cannot leave a tcpdump running.
    /// </summary>
    private async Task FinishCaptureAsync(MaldexDbContext db, AnalysisTask task, CaptureSession capture)
    {
        if (capture.Handle is not { } handle) return;
        capture.Handle = null;
        var none = CancellationToken.None;
        var problems = new List<string>();
        try
        {
            IReadOnlyList<CapturedFile> files = [];
            try { files = await handle.StopAsync(none).ConfigureAwait(false); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Stopping the capture of task {TaskId} failed.", task.Id);
                problems.Add($"the capture could not be stopped cleanly: {ex.Message}");
            }

            var now = time.GetUtcNow();
            var flowsStored = false;
            foreach (var file in files)
            {
                try
                {
                    var (category, max) = file.Kind == ArtifactKind.Pcap ? ("pcap", CaptureService.DefaultMaxPcapBytes) : ("flows", NetnsEgressRouter.MaxFlowsBytes);
                    await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    var blob = await artifacts.PutAsync(category, stream, max, none).ConfigureAwait(false);
                    db.Artifacts.Add(new Artifact
                    {
                        Id = Guid.NewGuid(), TaskId = task.Id, Kind = file.Kind, BlobKey = blob.Key, Sha256 = blob.Sha256, Size = blob.Size, CreatedAt = now,
                    });
                    flowsStored |= file.Kind == ArtifactKind.MitmFlows;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Storing the {Kind} capture of task {TaskId} failed.", file.Kind, task.Id);
                    problems.Add($"the {file.Kind} capture could not be stored: {ex.Message}");
                }
            }
            // Only our own rows: unrelated pending changes are not flushed here.
            if (db.ChangeTracker.Entries<Artifact>().Any(e => e.State == EntityState.Added)) await db.SaveChangesAsync(none).ConfigureAwait(false);
            // Saved: the router need not keep its own (second, up to 512 MiB) copy of the flows at release.
            if (flowsStored)
            {
                try { NetnsEgressRouter.MarkFlowsStored(netOptions.Value.RunDir, task.Id); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not mark the flows of task {TaskId} as stored; the router keeps a copy.", task.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Saving the capture artifacts of task {TaskId} failed.", task.Id);
            // Never leave the rejected rows pending: they would fail the next save of this run too.
            foreach (var entry in db.ChangeTracker.Entries<Artifact>().Where(e => e.State == EntityState.Added).ToList()) entry.State = EntityState.Detached;
            problems.Add($"the capture artifacts could not be saved: {ex.Message}");
        }
        finally
        {
            try { await handle.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { logger.LogWarning(ex, "Cleaning up the capture of task {TaskId} failed.", task.Id); }
        }

        capture.Stored = problems.Count == 0;
        problems.AddRange(handle.Warnings); // truncation etc.: reported, but nothing was lost by storing
        if (problems.Count == 0) return;
        try { await AddWarningsAsync(db, task, problems).ConfigureAwait(false); }
        catch (Exception ex) { logger.LogError(ex, "Could not record the capture warnings of task {TaskId}.", task.Id); }
    }

    /// <summary>Appends warnings that processing keeps (see <see cref="AnalysisTask.CaptureWarningPrefix"/>). The task row is also written by the API, so reload and retry.</summary>
    private async Task AddWarningsAsync(MaldexDbContext db, AnalysisTask task, List<string> problems)
    {
        for (var tries = 1; ; tries++)
        {
            await db.Entry(task).ReloadAsync(CancellationToken.None).ConfigureAwait(false);
            task.ProcessingWarnings = [.. task.ProcessingWarnings, .. problems.Select(p => GuestText.Clean(AnalysisTask.CaptureWarningPrefix + p, 2000))];
            try
            {
                await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException) when (tries < MaxTransitionAttempts) { }
        }
    }

    /// <summary>The run's leftovers (lease record, logs, the router's own copy of the flows) once the lease is released. Best effort.</summary>
    private void CleanupRunNetDir(Guid taskId)
    {
        try
        {
            var dir = Path.Combine(Path.GetFullPath(netOptions.Value.RunDir), "net", RunNetNames.For(taskId).Namespace);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the network run directory of task {TaskId}.", taskId);
        }
    }

    /// <summary>
    /// Runs the isolation self-test and records the verdict in the task's audit log. A failed (or impossible) test throws
    /// <see cref="IsolationException"/>, which fails the run in Preparing without a retry.
    /// </summary>
    private async Task RunSelfTestAsync(MaldexDbContext db, AnalysisTask task, NetworkLease lease, IAgentChannel channel, TransferInbox inbox, CancellationToken ct)
    {
        IsolationVerdict verdict;
        try
        {
            verdict = await EvaluateIsolationAsync(db, task, lease, channel, inbox, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or SelfTestStoppedException))
        {
            // Any unexpected error is a failed self-test (and is audited), never a pass.
            logger.LogError(ex, "Isolation self-test of task {TaskId} failed unexpectedly.", task.Id);
            verdict = IsolationVerdict.Fail($"self-test error: {GuestText.Clean(ex.Message, 200)}");
        }

        db.AuditLogs.Add(new AuditLog
        {
            At = time.GetUtcNow(),
            Action = verdict.Isolated ? "isolation.selftest.passed" : "isolation.selftest.failed",
            Target = task.Id.ToString(),
            Details = $"mode={task.NetworkMode}; {(verdict.Isolated ? "guest-reported probes all blocked" : "not isolated: " + GuestText.Clean(verdict.Reason, 500))}",
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (!verdict.Isolated) throw new IsolationException($"isolation self-test failed: {GuestText.Clean(verdict.Reason, 500)}");
    }

    private async Task<IsolationVerdict> EvaluateIsolationAsync(MaldexDbContext db, AnalysisTask task, NetworkLease lease, IAgentChannel channel, TransferInbox inbox, CancellationToken ct)
    {
        var info = await networkInfo.GetAsync(lease, ct).ConfigureAwait(false);
        if (info is null) return IsolationVerdict.Fail("the run's network addressing is unknown");
        if (info.Mode != task.NetworkMode) return IsolationVerdict.Fail($"network mode mismatch: lease {info.Mode}, task {task.NetworkMode}");

        var plan = IsolationProbePlan.Build(netOptions.Value, info);
        if (selfTestOptions.Value.RequireControlPlaneProbes && !plan.Targets.Any(t => t.Name.StartsWith("control_plane_", StringComparison.Ordinal)))
            return IsolationVerdict.Fail("no control-plane probe targets configured");

        var expectedEgressIp = task.NetworkMode == NetworkMode.LiveVpn
            ? await expectedEgress.GetAsync(task.NetworkMode, task.ExitCountry, ct).ConfigureAwait(false)
            : null;
        return await new SelfTestGate(time, pollInterval: _options.ControlPollInterval)
            .RunAsync(channel, inbox, task.NetworkMode, expectedEgressIp, plan, ct, async token =>
                await db.Tasks.AsNoTracking().Where(t => t.Id == task.Id).Select(t => t.StopRequestedAt).SingleAsync(token).ConfigureAwait(false) is not null)
            .ConfigureAwait(false);
    }

    /// <summary>Streams the stored sample into the guest (it is opened later, once Running). Any failure fails the run in Preparing (no retry).</summary>
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

    /// <summary>True when a lease existed and its release succeeded.</summary>
    private async Task<bool> ReleaseLeaseAsync(Guid taskId, NetworkLease? lease)
    {
        if (lease is null) return false;
        try
        {
            await egress.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Releasing network lease for {TaskId} failed.", taskId);
            return false;
        }
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

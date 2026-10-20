using Maldex.Infrastructure.Real;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Maldex.Core.Tests.Real;

/// <summary>
/// The host only watches ExecuteTask of BackgroundServices it registered itself; the gated wrapper must surface a fault of the
/// service it wraps (critical log + stop the application) instead of letting the orchestrator die silently.
/// </summary>
public class GatedHostedServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopped.TrySetResult();
    }

    private sealed class Gate(bool allow) : IWorkerStartGate
    {
        public Task<bool> AllowStartAsync(CancellationToken ct) => Task.FromResult(allow);
    }

    private sealed class RecordingLog : ILogger<GatedHostedService<IHostedService>>
    {
        public readonly List<(LogLevel Level, string Message, Exception? Error)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception)); }
    }

    private sealed class ThrowingWorker(TaskCompletionSource release) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await release.Task.WaitAsync(stoppingToken);
            throw new InvalidOperationException("database is gone");
        }
    }

    private sealed class QuietWorker : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private sealed class FailingStart : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new IOException("virsh hiccup");
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static GatedHostedService<IHostedService> Wrap(IHostedService inner, Lifetime life, RecordingLog log, bool allow = true) =>
        new(inner, new Gate(allow), life, log);

    [Fact]
    public async Task FaultedBackgroundService_LogsCritical_AndStopsTheApplication()
    {
        var release = new TaskCompletionSource();
        var life = new Lifetime();
        var log = new RecordingLog();
        var svc = Wrap(new ThrowingWorker(release), life, log);
        await svc.StartAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(life.Stopped.Task.IsCompleted); // running fine so far
        release.SetResult();
        await life.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        lock (log.Entries)
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Critical && e.Error is InvalidOperationException && e.Message.Contains("ThrowingWorker"));
        await svc.StopAsync(Ct);
    }

    [Fact]
    public async Task FailedStart_LogsCritical_AndStopsTheApplication()
    {
        var life = new Lifetime();
        var log = new RecordingLog();
        await Wrap(new FailingStart(), life, log).StartAsync(Ct);
        await life.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        lock (log.Entries) Assert.Contains(log.Entries, e => e.Level == LogLevel.Critical && e.Error is IOException);
    }

    [Fact]
    public async Task NormalShutdown_DoesNotStopTheApplication()
    {
        var life = new Lifetime();
        var log = new RecordingLog();
        var svc = Wrap(new QuietWorker(), life, log);
        await svc.StartAsync(Ct);
        await Task.Delay(50, Ct);
        await svc.StopAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(life.Stopped.Task.IsCompleted);
        lock (log.Entries) Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task GateDenied_WebAppStaysUp()
    {
        var life = new Lifetime();
        var svc = Wrap(new FailingStart(), life, new RecordingLog(), allow: false);
        await svc.StartAsync(Ct);
        await Task.Delay(50, Ct);
        await svc.StopAsync(Ct);
        Assert.False(life.Stopped.Task.IsCompleted);
    }
}

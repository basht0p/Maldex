using Maldex.Core.Abstractions;

namespace Maldex.Infrastructure.Fakes;

/// <summary>Fake mode has no packets to record: the handle stops with an empty list.</summary>
public sealed class NoopCaptureService : ICaptureService
{
    public Task<ICaptureHandle> StartAsync(NetworkLease lease, CancellationToken ct) => Task.FromResult<ICaptureHandle>(new Handle());

    private sealed class Handle : ICaptureHandle
    {
        public Task<IReadOnlyList<CapturedFile>> StopAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapturedFile>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

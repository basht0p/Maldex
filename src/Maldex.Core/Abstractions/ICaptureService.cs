using Maldex.Core.Domain;

namespace Maldex.Core.Abstractions;

/// <summary>A file produced by a run's capture: a PCAP or the MITM flow log. The path is a run-owned local file.</summary>
public sealed record CapturedFile(ArtifactKind Kind, string Path);

/// <summary>The capture could not be started; the run must not detonate without it.</summary>
public sealed class CaptureStartException(string message, Exception? inner = null) : Exception(message, inner);

public interface ICaptureService
{
    /// <summary>Starts recording the run's traffic (packets and HTTP(S) flows). Throws <see cref="CaptureStartException"/>.</summary>
    Task<ICaptureHandle> StartAsync(NetworkLease lease, CancellationToken ct);
}

/// <summary>
/// A running capture. <see cref="StopAsync"/> stops it and returns the non-empty files (idempotent: a second call returns
/// the same list). <see cref="IAsyncDisposable.DisposeAsync"/> is the best-effort clean-up: it stops the capture if it is
/// still running and then deletes the files, so the caller disposes only after it has stored them.
/// </summary>
public interface ICaptureHandle : IAsyncDisposable
{
    Task<IReadOnlyList<CapturedFile>> StopAsync(CancellationToken ct);
}

namespace Maldex.Processing;

/// <summary>Processes one task in <c>Processing</c>. A seam so the worker's retry policy can be tested.</summary>
public interface IProcessingPipeline
{
    Task ProcessAsync(Guid taskId, CancellationToken ct);
}

using Maldex.Core.Verdicts;

namespace Maldex.Core.Abstractions;

public interface ISuricataRunner
{
    /// <summary>Replays a capture through Suricata and returns its alerts as detections.</summary>
    Task<IReadOnlyList<DetectionResult>> RunAsync(string pcapPath, CancellationToken ct);
}

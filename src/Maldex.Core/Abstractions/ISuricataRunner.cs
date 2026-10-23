using Maldex.Core.Verdicts;

namespace Maldex.Core.Abstractions;

public interface ISuricataRunner
{
    /// <summary>Replays a capture through Suricata and returns its alerts as detections, and the flows it saw.</summary>
    Task<SuricataResult> RunAsync(string pcapPath, CancellationToken ct);
}

/// <summary>What one Suricata replay found. Both lists are capped (guest traffic is hostile).</summary>
public sealed record SuricataResult(IReadOnlyList<DetectionResult> Alerts, IReadOnlyList<SuricataFlow> Flows)
{
    public static SuricataResult Empty { get; } = new([], []);
}

/// <summary>One flow record from <c>eve.json</c>; <see cref="Bytes"/> counts both directions. Guest-derived text.</summary>
public sealed record SuricataFlow(string Proto, string Src, string Dst, int DstPort, long Bytes, DateTimeOffset StartedAt);

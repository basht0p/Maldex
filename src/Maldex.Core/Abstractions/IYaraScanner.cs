using Maldex.Core.Verdicts;

namespace Maldex.Core.Abstractions;

public interface IYaraScanner
{
    /// <summary>Scans a file on disk and returns one detection per matching rule. Throws if the scanner itself cannot run.</summary>
    /// <param name="filePath">Filesystem path to scan.</param>
    /// <param name="target">What the detections are reported against (for example the sample's file name).</param>
    Task<IReadOnlyList<DetectionResult>> ScanAsync(string filePath, string target, CancellationToken ct);
}

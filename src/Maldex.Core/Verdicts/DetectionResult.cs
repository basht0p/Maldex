using Maldex.Core.Domain;

namespace Maldex.Core.Verdicts;

public sealed record DetectionResult(
    DetectionSource Source,
    string Rule,
    int Severity,
    IReadOnlyList<string> Tags,
    string Target,
    string? Details);

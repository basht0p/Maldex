using Maldex.Core.Domain;

namespace Maldex.Core.Verdicts;

public static class VerdictEngine
{
    public static Verdict Compute(IReadOnlyCollection<DetectionResult> detections)
    {
        var suspicious = false;
        foreach (var d in detections)
        {
            switch (d.Source)
            {
                case DetectionSource.Yara:
                    if (d.Tags.Any(t => string.Equals(t, "malicious", StringComparison.OrdinalIgnoreCase)))
                        return Verdict.Malicious;
                    suspicious = true;
                    break;
                case DetectionSource.Suricata:
                    if (d.Severity == 1) return Verdict.Malicious;
                    if (d.Severity == 2) suspicious = true;
                    break;
                case DetectionSource.Heuristic:
                    suspicious = true;
                    break;
            }
        }
        return suspicious ? Verdict.Suspicious : Verdict.NoThreats;
    }
}

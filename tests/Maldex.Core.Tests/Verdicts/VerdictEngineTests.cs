using Xunit;
using Maldex.Core.Domain;
using Maldex.Core.Verdicts;

namespace Maldex.Core.Tests.Verdicts;

public class VerdictEngineTests
{
    private static DetectionResult D(DetectionSource s, int sev, params string[] tags) =>
        new(s, "r", sev, tags, "t", null);

    [Fact] public void Yara_TaggedMalicious_IsMalicious() =>
        Assert.Equal(Verdict.Malicious, VerdictEngine.Compute([D(DetectionSource.Yara, 3, "Malicious")]));
    [Fact] public void Suricata_Sev1_IsMalicious() =>
        Assert.Equal(Verdict.Malicious, VerdictEngine.Compute([D(DetectionSource.Suricata, 1)]));
    [Fact] public void Yara_Untagged_IsSuspicious() =>
        Assert.Equal(Verdict.Suspicious, VerdictEngine.Compute([D(DetectionSource.Yara, 3, "packer")]));
    [Fact] public void Suricata_Sev2_IsSuspicious() =>
        Assert.Equal(Verdict.Suspicious, VerdictEngine.Compute([D(DetectionSource.Suricata, 2)]));
    [Fact] public void Suricata_Sev3_Alone_IsNoThreats() =>
        Assert.Equal(Verdict.NoThreats, VerdictEngine.Compute([D(DetectionSource.Suricata, 3)]));
    [Fact] public void AnyHeuristic_IsSuspicious() =>
        Assert.Equal(Verdict.Suspicious, VerdictEngine.Compute([D(DetectionSource.Heuristic, 2, "heuristic")]));
    [Fact] public void Empty_IsNoThreats() =>
        Assert.Equal(Verdict.NoThreats, VerdictEngine.Compute([]));
    [Fact] public void Malicious_Wins_OverSuspicious() =>
        Assert.Equal(Verdict.Malicious, VerdictEngine.Compute([D(DetectionSource.Heuristic, 2), D(DetectionSource.Suricata, 1), D(DetectionSource.Yara, 2)]));
}

using Maldex.Core.Domain;
using Maldex.Infrastructure.Scanning;
using Xunit;

namespace Maldex.Core.Tests.Scanning;

public class SuricataEveParserTests
{
    private const string Alert =
        """{"timestamp":"2026-10-19T10:00:00.000000+0000","event_type":"alert","src_ip":"10.213.0.2","src_port":49152,"dest_ip":"203.0.113.9","dest_port":80,"proto":"TCP","alert":{"action":"allowed","gid":1,"signature_id":9000001,"rev":1,"signature":"MALDEX TEST controlled URL","category":"A Network Trojan was detected","severity":1}}""";

    [Fact]
    public void Parse_Sev1Alert_MapsSeverity1()
    {
        var result = SuricataEveParser.Parse(Alert + "\n");

        var d = Assert.Single(result);
        Assert.Equal(DetectionSource.Suricata, d.Source);
        Assert.Equal("MALDEX TEST controlled URL", d.Rule);
        Assert.Equal(1, d.Severity);
        Assert.Equal(["A Network Trojan was detected"], d.Tags);
        Assert.Equal("TCP 10.213.0.2:49152 → 203.0.113.9:80", d.Target);
        Assert.Equal("sid 9000001 rev 1", d.Details);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    public void Parse_OtherSeverities(int eve, int expected)
    {
        var line = Alert.Replace("\"severity\":1", $"\"severity\":{eve}");
        Assert.Equal(expected, Assert.Single(SuricataEveParser.Parse(line)).Severity);
    }

    [Fact]
    public void Parse_IgnoresNonAlertEvents()
    {
        var eve = """{"event_type":"http","src_ip":"1.1.1.1"}""" + "\n" + Alert + "\n" + """{"event_type":"stats"}""";
        Assert.Single(SuricataEveParser.Parse(eve));
    }

    [Fact]
    public void Parse_SkipsMalformedLines()
    {
        var eve = "not json\n{\"event_type\":\"alert\",\"alert\":\n[1,2]\n" + Alert + "\n{\"event_type\":\"alert\"}\n";
        Assert.Single(SuricataEveParser.Parse(eve));
    }

    [Fact]
    public void Parse_CapsAt1000Alerts()
    {
        var eve = string.Join("\n", Enumerable.Repeat(Alert, 1500));
        Assert.Equal(1000, SuricataEveParser.Parse(eve).Count);
        Assert.Equal(5, SuricataEveParser.Parse(eve.Split('\n'), 5).Count);
    }

    [Fact]
    public void Parse_NoCategory_EmptyTags()
    {
        var line = Alert.Replace("\"category\":\"A Network Trojan was detected\",", "");
        Assert.Empty(Assert.Single(SuricataEveParser.Parse(line)).Tags);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    public void Parse_Empty_ReturnsEmpty(string input) => Assert.Empty(SuricataEveParser.Parse(input));
}

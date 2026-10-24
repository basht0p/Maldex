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

    private const string Flow =
        """{"timestamp":"2026-10-23T19:33:12.500000+0000","event_type":"flow","src_ip":"10.213.0.2","src_port":49700,"dest_ip":"198.18.0.10","dest_port":80,"proto":"TCP","app_proto":"http","flow":{"pkts_toserver":6,"pkts_toclient":4,"bytes_toserver":600,"bytes_toclient":900,"start":"2026-10-23T19:33:12.123456+0000","end":"2026-10-23T19:33:13.000000+0000","state":"closed"}}""";

    [Fact]
    public void ParseEve_Flow_MapsFields()
    {
        var r = SuricataEveParser.ParseEve([Flow]);

        Assert.Empty(r.Alerts);
        var f = Assert.Single(r.Flows);
        Assert.Equal("TCP", f.Proto);
        Assert.Equal("10.213.0.2", f.Src);
        Assert.Equal("198.18.0.10", f.Dst);
        Assert.Equal(80, f.DstPort);
        Assert.Equal(1500, f.Bytes);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 19, 33, 12, 123, TimeSpan.Zero).AddTicks(4560), f.StartedAt);
    }

    [Fact]
    public void ParseEve_AlertsAndFlows_InOnePass()
    {
        var r = SuricataEveParser.ParseEve([Alert, Flow, """{"event_type":"dns"}"""]);
        Assert.Single(r.Alerts);
        Assert.Single(r.Flows);
    }

    [Theory]
    [InlineData("""{"event_type":"flow","src_ip":"10.0.0.1","dest_ip":"10.0.0.2","dest_port":80,"proto":"TCP"}""")] // no flow object
    [InlineData("""{"event_type":"flow","dest_ip":"10.0.0.2","dest_port":80,"proto":"TCP","flow":{"bytes_toserver":1,"bytes_toclient":1,"start":"2026-10-23T19:33:12.123456+0000"}}""")] // no source
    [InlineData("""{"event_type":"flow","src_ip":"10.0.0.1","dest_ip":"10.0.0.2","dest_port":"x","proto":"TCP","flow":{"bytes_toserver":1,"bytes_toclient":1,"start":"2026-10-23T19:33:12.123456+0000"}}""")] // port not a number
    [InlineData("""{"event_type":"flow","src_ip":"10.0.0.1","dest_ip":"10.0.0.2","dest_port":80,"proto":"TCP","flow":{"bytes_toserver":1,"bytes_toclient":1,"start":"yesterday"}}""")] // bad start
    public void ParseEve_MalformedFlow_IsSkipped(string line) => Assert.Empty(SuricataEveParser.ParseEve([line]).Flows);

    [Fact]
    public void ParseEve_ICMPFlowWithoutPort_IsKeptWithPortZero()
    {
        var icmp = Flow.Replace("\"dest_port\":80,", "").Replace("\"proto\":\"TCP\"", "\"proto\":\"ICMP\"");
        Assert.Equal(0, Assert.Single(SuricataEveParser.ParseEve([icmp]).Flows).DstPort);
    }

    [Fact]
    public void ParseEve_Flows_AreCapped()
    {
        var r = SuricataEveParser.ParseEve(Enumerable.Repeat(Flow, 30), maxAlerts: 5, maxFlows: 7);
        Assert.Equal(7, r.Flows.Count);
    }

    [Fact]
    public void ParseEve_StopsReading_OnceBothCapsAreReached()
    {
        var read = 0;
        IEnumerable<string> Lines() { while (true) { read++; yield return read % 2 == 0 ? Alert : Flow; } }
        var r = SuricataEveParser.ParseEve(Lines(), maxAlerts: 3, maxFlows: 3);
        Assert.Equal(3, r.Alerts.Count);
        Assert.Equal(3, r.Flows.Count);
        Assert.True(read <= 7, $"read {read} lines");
    }
}

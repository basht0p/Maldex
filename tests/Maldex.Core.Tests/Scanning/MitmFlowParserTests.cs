using System.Text;
using Maldex.Infrastructure.Scanning;
using Xunit;

namespace Maldex.Core.Tests.Scanning;

public class MitmFlowParserTests
{
    private static MemoryStream Lines(params string[] lines) => new(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));

    private const string Full =
        """{"ts":1760000000.5,"method":"POST","url":"http://evil.test/a?b=1","host":"evil.test","status":200,"req_headers":[["Host","evil.test"],["X-A","1"]],"resp_headers":[["Content-Type","text/plain"]],"req_body_b64":"AAAA","resp_body_b64":"aGVsbG8=","tls":false,"truncated":false}""";

    [Fact]
    public void Parse_ExtractsRequestResponse()
    {
        var flow = Assert.Single(MitmFlowParser.Parse(Lines(Full)));

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_500), flow.At);
        Assert.Equal("POST", flow.Method);
        Assert.Equal("http://evil.test/a?b=1", flow.Url);
        Assert.Equal("evil.test", flow.Host);
        Assert.Equal(200, flow.Status);
        Assert.Equal(new KeyValuePair<string, string>("X-A", "1"), flow.RequestHeaders[1]);
        Assert.Equal(new KeyValuePair<string, string>("Content-Type", "text/plain"), Assert.Single(flow.ResponseHeaders));
        Assert.Equal("hello", Encoding.ASCII.GetString(flow.ResponseBody!));
        Assert.False(flow.Tls);
    }

    [Fact]
    public void Parse_TlsFlow_HasHttpsUrl()
    {
        var line = """{"ts":1760000001,"method":"GET","url":"https://secure.test/","host":"secure.test","status":null,"req_headers":[],"resp_headers":[],"req_body_b64":null,"resp_body_b64":null,"tls":true,"truncated":false}""";

        var flow = Assert.Single(MitmFlowParser.Parse(Lines(line)));

        Assert.True(flow.Tls);
        Assert.StartsWith("https://", flow.Url);
        Assert.Null(flow.Status);
        Assert.Null(flow.ResponseBody);
    }

    [Fact]
    public void Parse_SkipsMalformed()
    {
        var result = MitmFlowParser.Parse(Lines("not json", "", "[1,2]", """{"ts":"x","method":"GET"}""", Full, """{"ts":1,"method":"GET","url":"u","host":"h","resp_body_b64":"!!!notbase64"}"""));

        Assert.Equal(2, result.Count);
        Assert.Equal("POST", result[0].Method);
        Assert.Null(result[1].ResponseBody); // a bad body does not lose the flow
    }

    [Fact]
    public void Parse_CapsFlowCount_AndReportsIgnored()
    {
        var lines = Enumerable.Repeat(Full, MitmFlowParser.MaxFlows + 5).ToArray();

        var result = MitmFlowParser.ParseWithStats(Lines(lines));

        Assert.Equal(MitmFlowParser.MaxFlows, result.Flows.Count);
        Assert.Equal(5, result.Ignored);
    }

    private static string Mk(string method = "GET", string url = "http://h/", string hdrValue = "v", string? body = null, int headers = 1)
    {
        var hs = string.Join(",", Enumerable.Range(0, headers).Select(i => $"[\"H{i}\",\"{hdrValue}\"]"));
        var b = body is null ? "null" : $"\"{body}\"";
        return $$"""{"ts":1,"method":"{{method}}","url":"{{url}}","host":"h","status":200,"req_headers":[{{hs}}],"resp_headers":[],"resp_body_b64":{{b}},"tls":false}""";
    }

    [Theory]
    [InlineData("method")]
    [InlineData("url")]
    [InlineData("header")]
    public void Parse_LoneSurrogate_SkipsOnlyThatLine(string where)
    {
        var bad = where switch
        {
            "method" => Mk(method: "G\\udcffT"),
            "url" => Mk(url: "http://x/\\udcff"),
            _ => Mk(hdrValue: "a\\udcffb"),
        };

        var result = MitmFlowParser.Parse(Lines(bad, Mk(method: "PUT")));

        Assert.Equal("PUT", Assert.Single(result).Method);
    }

    [Fact]
    public void Parse_OversizeLine_IsSkippedWithoutBuffering()
    {
        var huge = Mk(url: "http://x/" + new string('a', 5000));

        var result = MitmFlowParser.ParseWithStats(Lines(Mk(method: "A"), huge, Mk(method: "B")), maxLineBytes: 1000);

        Assert.Equal(["A", "B"], result.Flows.Select(f => f.Method));
        Assert.Equal(1, result.LinesSkipped);
    }

    [Fact]
    public void Parse_OversizeBody_DropsBodyKeepsRow()
    {
        var big = Convert.ToBase64String(new byte[3000]);
        var ok = Convert.ToBase64String(new byte[10]);

        var result = MitmFlowParser.ParseWithStats(Lines(Mk(body: big), Mk(body: ok)), maxBodyBytes: 1000);

        Assert.Equal(2, result.Flows.Count);
        Assert.Null(result.Flows[0].ResponseBody);
        Assert.Equal(10, result.Flows[1].ResponseBody!.Length);
        Assert.Equal(1, result.BodiesDropped);
    }

    [Fact]
    public void Parse_TotalBodyBudget_DropsBodiesBeyondIt()
    {
        var b = Convert.ToBase64String(new byte[100]);

        var result = MitmFlowParser.ParseWithStats(Lines(Mk(body: b), Mk(body: b), Mk(body: b)), bodyBudget: 250);

        Assert.Equal(3, result.Flows.Count);
        Assert.Equal(2, result.Flows.Count(f => f.ResponseBody is not null));
        Assert.Equal(1, result.BodiesDropped);
        Assert.Equal(200, result.BodyBytes);
    }

    [Fact]
    public void Parse_CapsHeaderPairs()
    {
        var flow = Assert.Single(MitmFlowParser.Parse(Lines(Mk(headers: 500))));

        Assert.Equal(MitmFlowParser.MaxHeaders, flow.RequestHeaders.Count);
    }
}

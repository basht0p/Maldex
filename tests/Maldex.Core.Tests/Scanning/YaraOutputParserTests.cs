using Maldex.Infrastructure.Scanning;
using Xunit;

namespace Maldex.Core.Tests.Scanning;

public class YaraOutputParserTests
{
    [Fact]
    public void Parse_RuleWithTags()
    {
        var result = YaraCliScanner.ParseOutput("EICAR_Test_File [malicious,test] /tmp/sample.bin\nOther_Rule [] /tmp/sample.bin\n");

        Assert.Equal(2, result.Count);
        Assert.Equal("EICAR_Test_File", result[0].Rule);
        Assert.Equal(["malicious", "test"], result[0].Tags);
        Assert.Equal("Other_Rule", result[1].Rule);
        Assert.Empty(result[1].Tags);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("   \r\n")]
    public void Parse_EmptyOutput(string stdout) => Assert.Empty(YaraCliScanner.ParseOutput(stdout));

    [Fact]
    public void Parse_IgnoresWarningsLines()
    {
        const string stdout =
            "warning: rule \"X\" in /rules/a.yar(3): string \"$a\" may slow down scanning\r\n" +
            "error: could not open file\n" +
            "Rule_One [malware] /tmp/a b/sample.bin\n" +
            "0x10:$a: some string match\n";

        var result = YaraCliScanner.ParseOutput(stdout);

        var only = Assert.Single(result);
        Assert.Equal("Rule_One", only.Rule);
        Assert.Equal(["malware"], only.Tags);
    }
}

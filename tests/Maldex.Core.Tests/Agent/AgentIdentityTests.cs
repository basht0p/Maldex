using Maldex.Core.Agent;
using Xunit;

namespace Maldex.Core.Tests.Agent;

public class AgentIdentityTests
{
    [Theory]
    [InlineData("1.2.3+abcdef", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData(" 4.5.6\n", "4.5.6")]
    [InlineData("", "0.0.0")]
    [InlineData(null, "0.0.0")]
    public void VersionOf_DropsBuildMetadata(string? input, string expected) =>
        Assert.Equal(expected, AgentIdentity.VersionOf(input));

    [Fact]
    public void BuildId_IsVersionPlusFirst12HexOfSha256_ForBytesAndStreamAlike()
    {
        var bytes = "abc"u8.ToArray();
        Assert.Equal("1.2.3+ba7816bf8f01", AgentIdentity.BuildId("1.2.3", bytes));
        Assert.Equal("1.2.3+ba7816bf8f01", AgentIdentity.BuildId("1.2.3", new MemoryStream(bytes)));
        Assert.Equal("1.2.3+000000000000", AgentIdentity.UnknownBuildId("1.2.3"));
    }

    [Fact]
    public void BuildIdOfFiles_IsTheVersionRecordWithoutMetadata_PlusTheExeHash()
    {
        var dir = Directory.CreateTempSubdirectory("maldex-agentid-").FullName;
        try
        {
            var exe = Path.Combine(dir, "Maldex.Agent.exe");
            var version = Path.Combine(dir, "Maldex.Agent.version");
            File.WriteAllBytes(exe, "abc"u8.ToArray());
            File.WriteAllText(version, "1.2.3+builder\n");
            Assert.Equal("1.2.3+ba7816bf8f01", AgentIdentity.BuildIdOfFiles(exe, version));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildIdOfFiles_MissingExe_Throws() =>
        Assert.Throws<FileNotFoundException>(() => AgentIdentity.BuildIdOfFiles(
            Path.Combine(Path.GetTempPath(), "maldex-no-such-" + Guid.NewGuid().ToString("N")), Path.Combine(Path.GetTempPath(), "maldex-no-such-version")));
}

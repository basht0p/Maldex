using System.Security.Cryptography;
using System.Text.Json;
using Maldex.Core.Agent;
using Xunit;

namespace Maldex.Core.Tests.Agent;

public class FileTransferValidatorTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.UnixEpoch;
    private static readonly string Sha = new('a', 64);

    private static FileChunkMessage Chunk(int index = 0, int count = 1, long total = 3, string? sha = null, string path = @"C:\x\a.exe", string data = "AQID") =>
        new(T, "t1", index, count, total, sha ?? Sha, path, data);

    [Fact]
    public void Valid_Chunk_ReturnsNull() => Assert.Null(AgentMessageValidator.Validate(Chunk()));

    [Fact]
    public void Valid_EmptyFile_ReturnsNull() => Assert.Null(AgentMessageValidator.Validate(Chunk(total: 0, data: "")));

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 65)]
    [InlineData(70, 65)]
    public void Validator_RejectsNegativeIndexOrCountMismatch(int index, int count) =>
        Assert.NotNull(AgentMessageValidator.Validate(Chunk(index, count)));

    [Theory]
    [InlineData(-1L)]
    [InlineData(16L * 1024 * 1024 + 1)]
    public void Validator_RejectsBadTotalBytes(long total) =>
        Assert.NotNull(AgentMessageValidator.Validate(Chunk(total: total)));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // uppercase
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Validator_RejectsBadSha(string sha) => Assert.NotNull(AgentMessageValidator.Validate(Chunk(sha: sha)));

    [Fact]
    public void Validator_RejectsOversizeChunk()
    {
        var big = Convert.ToBase64String(new byte[512 * 1024 + 1]);
        Assert.NotNull(AgentMessageValidator.Validate(Chunk(total: 600_000, data: big)));
        var max = Convert.ToBase64String(new byte[512 * 1024]);
        Assert.Null(AgentMessageValidator.Validate(Chunk(total: 600_000, data: max)));
    }

    [Fact]
    public void Validator_RejectsBadBase64_AndMissingFields()
    {
        Assert.NotNull(AgentMessageValidator.Validate(Chunk(data: "***not base64***")));
        Assert.NotNull(AgentMessageValidator.Validate(Chunk(path: "")));
        Assert.NotNull(AgentMessageValidator.Validate(new FileChunkMessage(T, "", 0, 1, 3, Sha, "p", "AQID")));
        Assert.NotNull(AgentMessageValidator.Validate(Chunk(total: 2))); // decoded 3 bytes exceed declared total
    }

    [Fact]
    public void WorstCase_FullChunk_FitsOneFrame_AsMessageAndCommand()
    {
        // 0xFB 0xEF 0xBE encodes to "++++": the worst case for JSON escaping of '+'.
        var raw = new byte[512 * 1024];
        for (var i = 0; i < raw.Length; i += 3)
        {
            raw[i] = 0xFB;
            if (i + 1 < raw.Length) raw[i + 1] = 0xEF;
            if (i + 2 < raw.Length) raw[i + 2] = 0xBE;
        }
        var b64 = Convert.ToBase64String(raw);
        var sha = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();

        var msg = JsonSerializer.SerializeToUtf8Bytes<AgentMessage>(new FileChunkMessage(T, new string('x', 64), 31, 32, raw.Length, sha, new string('p', 4096), b64), AgentJson.Options);
        var cmd = JsonSerializer.SerializeToUtf8Bytes<AgentCommand>(new PutFileChunkCommand(new string('c', 64), new string('t', 256), 63, 64, 32L * 1024 * 1024, sha, b64), AgentJson.Options);
        Assert.True(msg.Length <= AgentFrameReader.MaxFrameBytes, $"message frame {msg.Length}");
        Assert.True(cmd.Length <= AgentFrameReader.MaxFrameBytes, $"command frame {cmd.Length}");

        // And it still round-trips.
        var back = Assert.IsType<FileChunkMessage>(JsonSerializer.Deserialize<AgentMessage>(msg, AgentJson.Options));
        Assert.Equal(b64, back.DataBase64);
    }

    [Fact]
    public void NewCommands_RoundTrip()
    {
        var put = new PutFileChunkCommand("c", "t", 1, 3, 10, Sha, "AQID");
        var col = new CollectFileCommand("c2", @"C:\a");
        Assert.Equal(put, JsonSerializer.Deserialize<AgentCommand>(JsonSerializer.Serialize<AgentCommand>(put, AgentJson.Options), AgentJson.Options));
        Assert.Equal(col, JsonSerializer.Deserialize<AgentCommand>(JsonSerializer.Serialize<AgentCommand>(col, AgentJson.Options), AgentJson.Options));
        Assert.Contains("\"type\":\"put_file_chunk\"", JsonSerializer.Serialize<AgentCommand>(put, AgentJson.Options));
        Assert.Contains("\"type\":\"collect_file\"", JsonSerializer.Serialize<AgentCommand>(col, AgentJson.Options));
        Assert.Contains("\"type\":\"file_chunk\"", JsonSerializer.Serialize<AgentMessage>(Chunk(), AgentJson.Options));
    }
}

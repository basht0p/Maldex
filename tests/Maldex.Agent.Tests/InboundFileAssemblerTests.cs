using System.Security.Cryptography;
using Maldex.Agent.Transfers;
using Xunit;

namespace Maldex.Agent.Tests;

public class InboundFileAssemblerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mxagent-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void AssemblesChunks_AndVerifiesHash()
    {
        var data = new byte[700_000];
        new Random(1).NextBytes(data);
        using var a = new InboundFileAssembler(_dir, "s.bin", Convert.ToHexString(SHA256.HashData(data)));
        a.AddChunk(0, data.AsSpan(0, InboundFileAssembler.ChunkBytes));
        a.AddChunk(1, data.AsSpan(InboundFileAssembler.ChunkBytes));
        Assert.Equal(data, File.ReadAllBytes(a.Complete()));
    }

    [Fact]
    public void HashMismatch_DeletesFile_AndThrows()
    {
        using var a = new InboundFileAssembler(_dir, "s.bin", new string('0', 64));
        a.AddChunk(0, [1, 2, 3]);
        Assert.Throws<InvalidOperationException>(a.Complete);
        Assert.False(File.Exists(Path.Combine(_dir, "s.bin")));
    }

    [Fact]
    public void OutOfOrderChunk_Throws_AndFileNameCannotEscapeDirectory()
    {
        using var a = new InboundFileAssembler(_dir, @"..\..\evil.exe".Replace('\\', '/'), new string('0', 64));
        Assert.Equal(Path.Combine(_dir, "evil.exe"), a.Path);
        Assert.Throws<InvalidOperationException>(() => a.AddChunk(1, [1]));
    }
}

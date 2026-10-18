using Xunit;
using System.Text;
using Maldex.Infrastructure.Storage;
using ICSharpCode.SharpZipLib.Zip;

namespace Maldex.Core.Tests.Storage;

public class ProtectedZipTests
{
    private static async Task<byte[]> MakeZip(byte[] data, string? password = null)
    {
        using var output = new MemoryStream();
        using var src = new MemoryStream(data);
        if (password is null) await ProtectedZip.WriteAsync(src, "sample.bin", output);
        else await ProtectedZip.WriteAsync(src, "sample.bin", output, password);
        return output.ToArray();
    }

    [Fact]
    public async Task Zip_RoundTrips_WithPassword()
    {
        var data = Encoding.UTF8.GetBytes("malware bytes");
        var zip = await MakeZip(data);
        using var zf = new ZipFile(new MemoryStream(zip)) { Password = "infected" };
        var entry = zf.GetEntry("sample.bin");
        Assert.NotNull(entry);
        Assert.Equal(256, entry.AESKeySize);
        using var s = zf.GetInputStream(entry);
        var ms = new MemoryStream();
        await s.CopyToAsync(ms, TestContext.Current.CancellationToken);
        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public async Task Zip_WithoutPassword_Fails()
    {
        var zip = await MakeZip(Encoding.UTF8.GetBytes("secret"));
        using var zf = new ZipFile(new MemoryStream(zip));
        var entry = zf.GetEntry("sample.bin")!;
        Assert.Throws<ZipException>(() => zf.GetInputStream(entry));
    }

    [Fact]
    public async Task Zip_WrongPassword_Fails()
    {
        var zip = await MakeZip(Encoding.UTF8.GetBytes("secret"));
        using var zf = new ZipFile(new MemoryStream(zip)) { Password = "wrong" };
        var entry = zf.GetEntry("sample.bin")!;
        Assert.ThrowsAny<Exception>(() =>
        {
            using var s = zf.GetInputStream(entry);
            new MemoryStream().Let(m => s.CopyTo(m));
        });
    }
}

internal static class Ext { public static void Let<T>(this T t, Action<T> a) => a(t); }

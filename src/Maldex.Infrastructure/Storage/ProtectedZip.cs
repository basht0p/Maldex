using ICSharpCode.SharpZipLib.Zip;

namespace Maldex.Infrastructure.Storage;

public static class ProtectedZip
{
    /// <summary>Writes <paramref name="source"/> as a single AES-256 encrypted entry into a ZIP on <paramref name="output"/>.</summary>
    public static async Task WriteAsync(Stream source, string entryName, Stream output,
        string password = "infected", CancellationToken ct = default)
    {
        using var zip = new ZipOutputStream(output) { IsStreamOwner = false, Password = password };
        zip.PutNextEntry(new ZipEntry(entryName) { AESKeySize = 256 });
        await source.CopyToAsync(zip, 81920, ct);
        zip.CloseEntry();
        zip.Finish();
    }
}

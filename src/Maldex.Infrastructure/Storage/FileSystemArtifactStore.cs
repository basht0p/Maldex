using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Maldex.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Storage;

/// <summary>Bound from configuration section <c>Maldex:Artifacts</c>.</summary>
public sealed class ArtifactStoreOptions
{
    public const string SectionName = "Maldex:Artifacts";
    public string Root { get; set; } = ".data/artifacts";
}

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed partial class FileSystemArtifactStore : IArtifactStore
{
    [GeneratedRegex("^[a-z]+/[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f]{64}\\z")]
    private static partial Regex KeyRegex();

    [GeneratedRegex("^[a-z]+\\z")]
    private static partial Regex CategoryRegex();

    private const UnixFileMode ReadOnlyMode = UnixFileMode.UserRead | UnixFileMode.GroupRead;

    private readonly string _root;
    private readonly string _tmp;

    public FileSystemArtifactStore(IOptions<ArtifactStoreOptions> options)
    {
        _root = Path.GetFullPath(options.Value.Root);
        _tmp = Path.Combine(_root, "tmp");
    }

    public string GetPath(string key)
    {
        ValidateKey(key);
        return Path.Combine(_root, key);
    }

    public bool Exists(string key) => File.Exists(GetPath(key));

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = GetPath(key);
        if (!File.Exists(path)) throw new FileNotFoundException("Artifact not found.", key);
        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true));
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = GetPath(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<StoredBlob> PutAsync(string category, Stream content, long maxBytes, CancellationToken ct)
    {
        if (category is null || !CategoryRegex().IsMatch(category))
            throw new ArgumentException("Category must match ^[a-z]+$.", nameof(category));

        Directory.CreateDirectory(_tmp);
        var tempPath = Path.Combine(_tmp, Guid.NewGuid().ToString("N"));
        try
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            long size = 0;

            await using (var tmp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    size += read;
                    if (size > maxBytes) throw new BlobTooLargeException(maxBytes);
                    sha256.AppendData(buffer, 0, read);
                    sha1.AppendData(buffer, 0, read);
                    md5.AppendData(buffer, 0, read);
                    await tmp.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            var hash = Convert.ToHexStringLower(sha256.GetHashAndReset());
            var key = $"{category}/{hash[..2]}/{hash[2..4]}/{hash}";
            var dest = GetPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            if (File.Exists(dest))
            {
                File.Delete(tempPath);
            }
            else
            {
                File.SetUnixFileMode(tempPath, ReadOnlyMode);
                try
                {
                    File.Move(tempPath, dest, overwrite: false);
                }
                catch (IOException) when (File.Exists(dest))
                {
                    // Another writer stored identical content between our check and the move.
                    File.Delete(tempPath);
                }
            }

            return new StoredBlob(key, hash,
                Convert.ToHexStringLower(sha1.GetHashAndReset()),
                Convert.ToHexStringLower(md5.GetHashAndReset()), size);
        }
        catch
        {
            try { File.Delete(tempPath); } catch (IOException) { }
            throw;
        }
    }

    private static void ValidateKey(string key)
    {
        if (key is null || !KeyRegex().IsMatch(key))
            throw new ArgumentException("Invalid artifact key.", nameof(key));
    }
}

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

    private const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    private const UnixFileMode TempMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

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

    public async IAsyncEnumerable<(string Key, DateTimeOffset LastWrite)> ListAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        if (!Directory.Exists(_root)) yield break;
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var key = Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (!KeyRegex().IsMatch(key)) continue; // skips tmp/ and anything foreign
            yield return (key, new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
        }
    }

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

        EnsureDirectory(_tmp);
        var tempPath = Path.Combine(_tmp, Guid.NewGuid().ToString("N"));
        try
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            long size = 0;

            await using (var tmp = new FileStream(tempPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 81920,
                Options = FileOptions.Asynchronous,
                UnixCreateMode = TempMode,
            }))
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
            EnsureDirectory(Path.GetDirectoryName(dest)!);

            if (File.Exists(dest))
            {
                File.Delete(tempPath);
                // Refresh the age so a concurrent garbage-collection sweep can't delete an old orphan this upload now relies on.
                TryTouch(dest);
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
                    TryTouch(dest);
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

    private static void TryTouch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Creates <paramref name="dir"/> and every missing ancestor at or below the root with owner+group-only mode.
    /// (Directory.CreateDirectory applies the mode to the leaf only, so each level is created explicitly.)</summary>
    private void EnsureDirectory(string dir)
    {
        if (Directory.Exists(dir)) return;
        if (!string.Equals(dir, _root, StringComparison.Ordinal))
            EnsureDirectory(Path.GetDirectoryName(dir)!);
        Directory.CreateDirectory(dir, DirMode);
    }

    private static void ValidateKey(string key)
    {
        if (key is null || !KeyRegex().IsMatch(key))
            throw new ArgumentException("Invalid artifact key.", nameof(key));
    }
}

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Maldex.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Maldex.Infrastructure.Media;

/// <summary>Where media files live on disk: staging while receiving, then once under their SHA-256, read-only.</summary>
public sealed partial class MediaStore(IOptions<MediaOptions> options, IDiskSpaceProbe disk)
{
    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex Sha256Hex();

    private string Root => options.Value.Root;
    private string StagingDir => Path.Combine(Root, "staging");

    public string FinalPath(string sha256)
    {
        if (sha256 is null || !Sha256Hex().IsMatch(sha256)) throw new ArgumentException("not a lowercase hex SHA-256", nameof(sha256));
        return Path.Combine(Root, sha256 + ".iso");
    }

    public string StagingPath(Guid id) => Path.Combine(StagingDir, id.ToString("N") + ".part");

    /// <summary>Opens (creating it 0600) the staging file for appending after cutting it back to <paramref name="keepBytes"/>.</summary>
    public FileStream OpenStagingForAppend(Guid id, long keepBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keepBytes);
        Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(StagingDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(StagingDir);
        var open = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) open.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(StagingPath(id), open);
        try
        {
            if (stream.Length < keepBytes) throw new InvalidOperationException("the staging file is shorter than the bytes to keep");
            stream.SetLength(keepBytes);
            stream.Seek(0, SeekOrigin.End);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>SHA-256 state over the first <paramref name="length"/> bytes of the staging file, ready to take the next bytes.</summary>
    public async Task<IncrementalHash> RehashStagingAsync(Guid id, long length, CancellationToken ct)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using var file = new FileStream(StagingPath(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
            if (file.Length < length) throw new InvalidOperationException("the staging file is shorter than the expected length");
            var buffer = new byte[1 << 20];
            var remaining = length;
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (read == 0) throw new InvalidOperationException("the staging file ended early");
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            return hash;
        }
        catch
        {
            hash.Dispose();
            throw;
        }
    }

    /// <summary>Moves the staging file to its final name (0444). If that file already exists the staging file is dropped and the existing path returned.</summary>
    public string Finalize(Guid id, string sha256)
    {
        var final = FinalPath(sha256);
        var staging = StagingPath(id);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        try
        {
            File.Move(staging, final);
        }
        catch (IOException) when (File.Exists(final))
        {
            // Already there (earlier, or a concurrent finalize of the same content won): keep it, drop ours.
            File.Delete(staging);
        }
        return final;
    }

    public void DeleteStaging(Guid id) => File.Delete(StagingPath(id));

    public void DeleteFinal(string sha256) => File.Delete(FinalPath(sha256));

    public IEnumerable<Guid> StagingIds()
    {
        if (!Directory.Exists(StagingDir)) yield break;
        foreach (var file in Directory.EnumerateFiles(StagingDir, "*.part"))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id)) yield return id;
    }

    /// <summary>Free space on the media volume, in MiB.</summary>
    public long FreeMiB() => disk.FreeMiB(Root);
}

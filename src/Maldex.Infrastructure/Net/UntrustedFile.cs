using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Maldex.Infrastructure.Net;

public sealed record UntrustedCopyResult(long BytesCopied, bool Truncated);

/// <summary>
/// Copies a file that a less-trusted account (e.g. the mitm user, a guest-controlled daemon) could have replaced with
/// something hostile. Only a regular, single-link, non-symlink file is read; the open can never block; at most
/// <c>maxBytes</c> are copied.
/// </summary>
public static class UntrustedFile
{
    private const int O_RDONLY = 0, O_NOCTTY = 0x100, O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000;
    private const int ENOENT = 2;
    private const int AT_EMPTY_PATH = 0x1000;
    private const uint STATX_BASIC_STATS = 0x7ff;
    private const uint S_IFMT = 0xF000, S_IFREG = 0x8000;

    private static int ONoFollow =>
        RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x8000 : 0x20000;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buf);

    /// <exception cref="FileNotFoundException">The source does not exist (nothing to copy; <c>dest</c> is not created).</exception>
    /// <exception cref="IOException">The source is a symlink, not a regular file, has other hard links, or cannot be opened.</exception>
    public static async Task<UntrustedCopyResult> CopyRegularFileAsync(string source, string dest, long maxBytes, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(dest);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("UntrustedFile requires Linux.");
        ct.ThrowIfCancellationRequested();

        // O_NOFOLLOW: a symlink fails to open (ELOOP). O_NONBLOCK: a FIFO opens immediately instead of waiting for a writer.
        // O_NOCTTY: a terminal device planted in its place can never become our controlling terminal.
        var fd = Open(source, O_RDONLY | O_NOCTTY | O_NONBLOCK | O_CLOEXEC | ONoFollow);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == ENOENT) throw new FileNotFoundException($"'{source}' does not exist.", source);
            throw new IOException($"Cannot open '{source}' safely (errno {errno}); refusing (symlink?).");
        }
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        using (handle)
        {
            var buf = new byte[256];
            if (Statx(fd, "", AT_EMPTY_PATH, STATX_BASIC_STATS, buf) != 0)
                throw new IOException($"Cannot stat '{source}' (errno {Marshal.GetLastPInvokeError()}).");
            var nlink = BitConverter.ToUInt32(buf, 16);
            var mode = BitConverter.ToUInt16(buf, 28);
            if ((mode & S_IFMT) != S_IFREG) throw new IOException($"'{source}' is not a regular file; refusing.");
            if (nlink != 1) throw new IOException($"'{source}' has {nlink} hard links; refusing.");

            var fso = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) fso.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var input = new FileStream(handle, FileAccess.Read, 81920, isAsync: false);
            await using var output = new FileStream(dest, fso);
            var chunk = new byte[81920];
            long total = 0;
            while (total < maxBytes)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(chunk.Length, maxBytes - total);
                var n = await input.ReadAsync(chunk.AsMemory(0, want), ct);
                if (n == 0) return new UntrustedCopyResult(total, false);
                await output.WriteAsync(chunk.AsMemory(0, n), ct);
                total += n;
            }
            // Hit the cap: truncated only if there is more to read.
            var more = await input.ReadAsync(chunk.AsMemory(0, 1), ct);
            return new UntrustedCopyResult(total, more > 0);
        }
    }
}

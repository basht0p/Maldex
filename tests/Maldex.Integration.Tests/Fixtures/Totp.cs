using System.Security.Cryptography;

namespace Maldex.Integration.Tests.Fixtures;

/// <summary>RFC 6238 time-based one-time passwords (SHA-1, 30-second step, 6 digits), to answer the server's authenticator check in tests.</summary>
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Code(string base32Key, DateTimeOffset at)
    {
        var key = Base32Decode(base32Key);
        var counter = at.ToUnixTimeSeconds() / 30;
        Span<byte> message = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { message[i] = (byte)(counter & 0xff); counter >>= 8; }
        var hash = HMACSHA1.HashData(key, message);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string text)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('=').ToUpperInvariant())
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) continue;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)((buffer >> bits) & 0xff)); }
        }
        return [.. bytes];
    }
}

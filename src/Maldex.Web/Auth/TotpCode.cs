using System.Globalization;
using System.Security.Cryptography;

namespace Maldex.Web.Auth;

/// <summary>RFC 6238 TOTP codes as authenticator apps make them: HMAC-SHA-1, 30-second steps, 6 digits.</summary>
public static class TotpCode
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>The 6-digit code of time step <paramref name="step"/> (Unix seconds / 30) for <paramref name="secret"/>.</summary>
    public static string For(byte[] secret, long step)
    {
        Span<byte> message = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { message[i] = (byte)(step & 0xff); step >>= 8; }
        var hash = HMACSHA1.HashData(secret, message);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>RFC 4648 base32 (case-insensitive, padding optional; other characters are skipped), as Identity stores the key.</summary>
    public static byte[] Base32Decode(string text)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('=').ToUpperInvariant())
        {
            var v = Base32Alphabet.IndexOf(c);
            if (v < 0) continue;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)((buffer >> bits) & 0xff)); }
        }
        return [.. bytes];
    }
}

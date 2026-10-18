using System.Text;

namespace Maldex.Core.Text;

public static class GuestText
{
    private const string Suffix = "…[truncated]";

    /// <summary>Strips C0 controls (except tab, LF, CR) and DEL, then truncates to <paramref name="max"/> chars.</summary>
    public static string Clean(string? s, int max = 32_768)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var sb = new StringBuilder(Math.Min(s.Length, max + Suffix.Length));
        foreach (var c in s)
        {
            if (c < 0x20 && c is not ('\t' or '\n' or '\r')) continue;
            if (c == 0x7F) continue;
            sb.Append(c);
        }

        if (sb.Length <= max) return sb.ToString();

        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(sb[cut - 1])) cut--;
        return sb.ToString(0, cut) + Suffix;
    }
}

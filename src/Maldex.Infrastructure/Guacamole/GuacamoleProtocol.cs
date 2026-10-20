using System.Text;

namespace Maldex.Infrastructure.Guacamole;

/// <summary>
/// Guacamole protocol framing: an instruction is <c>LEN.VALUE,LEN.VALUE,...;</c> where LEN counts Unicode code points
/// and the first element is the opcode. The empty opcode is reserved for internal tunnel instructions (ping, tunnel UUID).
/// </summary>
public static class GuacamoleProtocol
{
    public const string InternalOpcode = "";

    public static string Encode(params string[] elements)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < elements.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CodePointCount(elements[i])).Append('.').Append(elements[i]);
        }
        return sb.Append(';').ToString();
    }

    public static int CodePointCount(string s)
    {
        var n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }
}

/// <summary>Reads whole Guacamole instructions from a text stream. Throws <see cref="FormatException"/> on malformed input.</summary>
public sealed class GuacamoleInstructionReader(TextReader source, int maxElementChars = 4 * 1024 * 1024)
{
    private readonly char[] _buffer = new char[8192];
    private int _pos;
    private int _len;

    /// <summary>The next instruction's elements (opcode first), or null at a clean end of input.</summary>
    public async Task<string[]?> ReadAsync(CancellationToken ct)
    {
        var elements = new List<string>();
        while (true)
        {
            // LEN
            var len = 0;
            var digits = 0;
            while (true)
            {
                var c = await NextAsync(ct);
                if (c < 0)
                {
                    if (elements.Count == 0 && digits == 0) return null;
                    throw new FormatException("Unexpected end of Guacamole stream.");
                }
                if (c == '.')
                {
                    if (digits == 0) throw new FormatException("Missing element length.");
                    break;
                }
                if (c is < '0' or > '9' || ++digits > 8) throw new FormatException("Invalid element length.");
                len = len * 10 + (c - '0');
                if (len > maxElementChars) throw new FormatException("Element too long.");
            }

            // VALUE: len code points
            var sb = new System.Text.StringBuilder(len);
            for (var i = 0; i < len; i++)
            {
                var c = await NextAsync(ct);
                if (c < 0) throw new FormatException("Unexpected end of Guacamole stream.");
                sb.Append((char)c);
                if (char.IsHighSurrogate((char)c))
                {
                    var low = await NextAsync(ct);
                    if (low < 0) throw new FormatException("Unexpected end of Guacamole stream.");
                    sb.Append((char)low);
                }
            }
            elements.Add(sb.ToString());

            var term = await NextAsync(ct);
            if (term == ';') return [.. elements];
            if (term != ',') throw new FormatException("Invalid element terminator.");
        }
    }

    private async ValueTask<int> NextAsync(CancellationToken ct)
    {
        if (_pos >= _len)
        {
            _len = await source.ReadAsync(_buffer.AsMemory(), ct);
            _pos = 0;
            if (_len == 0) return -1;
        }
        return _buffer[_pos++];
    }

    /// <summary>Parses every instruction in <paramref name="text"/>; a trailing partial instruction is an error.</summary>
    public static async Task<List<string[]>> ParseAllAsync(string text, CancellationToken ct)
    {
        var reader = new GuacamoleInstructionReader(new StringReader(text));
        var all = new List<string[]>();
        while (await reader.ReadAsync(ct) is { } instruction) all.Add(instruction);
        return all;
    }
}

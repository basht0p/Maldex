using System.Globalization;
using System.Text.RegularExpressions;

namespace Maldex.Core.Media;

/// <summary>
/// Reads the output of <c>wiminfo</c> for an install.wim. wiminfo prints the WIM's XML metadata raw, so the text comes from
/// untrusted media: a hostile value can contain newlines and forge lines that look like real keys. The parser therefore reads
/// only the "Available Images" section, only known keys at column 0, and refuses (empty list) any output that is ambiguous:
/// a repeated index, a repeated key inside one image, or an "Index:" line count that disagrees with "Image Count".
/// </summary>
public static partial class WimInfoParser
{
    private const int MaxImages = 64;
    private const int MaxValue = 100;

    [GeneratedRegex(@"^(Index|Name|Edition ID|Architecture|Build|Service Pack Build|Image Count):[ \t]*(.*)$")]
    private static partial Regex KnownKey();

    public static IReadOnlyList<InstallerImage> Parse(string output)
    {
        var images = new List<InstallerImage>();
        var seen = new HashSet<int>();
        Block? block = null;
        var inImages = false;
        int? declaredCount = null;
        var indexLines = 0;

        bool Flush()
        {
            if (block is not null && images.Count < MaxImages) images.Add(block.ToImage());
            block = null;
            return true;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Available Images", StringComparison.Ordinal)) { inImages = true; continue; }
            var m = KnownKey().Match(line);
            if (!m.Success) continue;
            var key = m.Groups[1].Value;
            var value = m.Groups[2].Value;

            if (key == "Image Count")
            {
                if (!inImages && declaredCount is null && int.TryParse(Clean(value), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) declaredCount = n;
                continue;
            }
            if (!inImages) continue;

            if (key == "Index")
            {
                Flush();
                indexLines++;
                if (int.TryParse(Clean(value), NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 1)
                {
                    if (!seen.Add(index)) return [];
                    block = new Block(index);
                }
                continue;
            }
            if (block is null) continue;
            if (!block.Set(key, Clean(value))) return [];
        }
        Flush();
        if (declaredCount is { } d && d != indexLines) return [];
        return images;
    }

    private static string Clean(string value)
    {
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > MaxValue ? cleaned[..MaxValue] : cleaned;
    }

    private sealed class Block(int index)
    {
        private string? _name, _editionId, _architecture, _build, _servicePackBuild;

        /// <summary>False when the key was already set in this image.</summary>
        public bool Set(string key, string value)
        {
            switch (key)
            {
                case "Name": if (_name is not null) return false; _name = value; break;
                case "Edition ID": if (_editionId is not null) return false; _editionId = value; break;
                case "Architecture": if (_architecture is not null) return false; _architecture = value; break;
                case "Build": if (_build is not null) return false; _build = value; break;
                case "Service Pack Build": if (_servicePackBuild is not null) return false; _servicePackBuild = value; break;
            }
            return true;
        }

        public InstallerImage ToImage()
        {
            var build = string.IsNullOrEmpty(_build) ? null
                : string.IsNullOrEmpty(_servicePackBuild) ? _build : $"{_build}.{_servicePackBuild}";
            return new InstallerImage(index, _name ?? "", NullIfEmpty(_editionId), build, NullIfEmpty(_architecture));
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }
}

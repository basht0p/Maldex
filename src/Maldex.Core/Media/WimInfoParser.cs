namespace Maldex.Core.Media;

/// <summary>Reads the output of <c>wiminfo</c> for an install.wim. The output comes from untrusted media, so every value is cleaned and bounded.</summary>
public static class WimInfoParser
{
    private const int MaxImages = 64;
    private const int MaxValue = 100;

    public static IReadOnlyList<InstallerImage> Parse(string output)
    {
        var images = new List<InstallerImage>();
        Block? block = null;

        void Flush()
        {
            if (block is not null && images.Count < MaxImages) images.Add(block.ToImage());
            block = null;
        }

        foreach (var line in output.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = Clean(line[(colon + 1)..]);

            if (key == "Index")
            {
                Flush();
                block = int.TryParse(value, out var index) ? new Block(index) : null;
                continue;
            }
            if (block is null) continue;
            switch (key)
            {
                case "Name": block.Name = value; break;
                case "Edition ID": block.EditionId = value; break;
                case "Architecture": block.Architecture = value; break;
                case "Build": block.Build = value; break;
                case "Service Pack Build": block.ServicePackBuild = value; break;
            }
        }
        Flush();
        return images;
    }

    private static string Clean(string value)
    {
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > MaxValue ? cleaned[..MaxValue] : cleaned;
    }

    private sealed class Block(int index)
    {
        public string Name = "";
        public string? EditionId, Architecture, Build, ServicePackBuild;

        public InstallerImage ToImage()
        {
            var build = string.IsNullOrEmpty(Build) ? null
                : string.IsNullOrEmpty(ServicePackBuild) ? Build : $"{Build}.{ServicePackBuild}";
            return new InstallerImage(index, Name, NullIfEmpty(EditionId), build, NullIfEmpty(Architecture));
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }
}

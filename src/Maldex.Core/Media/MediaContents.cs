using System.Text.Json;

namespace Maldex.Core.Media;

public sealed record InstallerImage(int Index, string Name, string? EditionId, string? Build, string? Architecture);

public sealed record MediaContents(IReadOnlyList<InstallerImage> Images, string? Application);

public static class MediaContentsJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(MediaContents contents) => JsonSerializer.Serialize(contents, Options);

    /// <summary>Null for missing or unreadable json: stored contents are advisory, never trusted to parse.</summary>
    public static MediaContents? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var contents = JsonSerializer.Deserialize<MediaContents>(json, Options);
            return contents?.Images is null ? null : contents;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

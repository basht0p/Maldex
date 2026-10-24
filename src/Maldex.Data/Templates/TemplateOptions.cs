namespace Maldex.Data.Templates;

public sealed class TemplateOptions
{
    public const string SectionName = "Maldex:Templates";

    /// <summary>How many previous Ready versions to keep per template, besides the current one.</summary>
    public int KeepPrevious { get; set; } = 1;
}

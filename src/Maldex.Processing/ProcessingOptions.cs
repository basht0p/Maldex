namespace Maldex.Processing;

/// <summary>Bound from <c>Maldex:Processing</c>.</summary>
public sealed class ProcessingOptions
{
    public const string SectionName = "Maldex:Processing";

    /// <summary>Attempts before a task whose processing keeps throwing is moved to Failed.</summary>
    public int MaxAttempts { get; set; } = 5;
}

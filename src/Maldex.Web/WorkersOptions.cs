namespace Maldex.Web;

/// <summary>Bound from <c>Maldex:Workers</c>. Background workers (Task 13) start only when Enabled.</summary>
public sealed class WorkersOptions
{
    public const string SectionName = "Maldex:Workers";
    public bool Enabled { get; set; } = true;
}

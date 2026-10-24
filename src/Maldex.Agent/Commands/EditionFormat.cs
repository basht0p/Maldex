namespace Maldex.Agent.Commands;

/// <summary>Pure formatting of the Windows edition string (kept apart from the Windows-only probe so it is testable anywhere).</summary>
public static class EditionFormat
{
    private const int FirstWindows11Build = 22000;

    /// <summary>
    /// Windows 11 still reports "Windows 10 ..." as ProductName, so a leading "Windows 10" becomes "Windows 11" from build 22000.
    /// Only when ProductName is missing is the name built from the EditionID.
    /// </summary>
    public static string FormatEdition(string? productName, string? editionId, string? currentBuild)
    {
        var windows11 = int.TryParse(currentBuild, out var build) && build >= FirstWindows11Build;
        var name = productName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            var edition = editionId?.Trim() switch
            {
                "Professional" => "Pro",
                "Enterprise" => "Enterprise",
                "EnterpriseEval" => "Enterprise Evaluation",
                "Core" => "Home",
                { Length: > 0 } other => other,
                _ => "",
            };
            name = ("Windows " + (windows11 ? "11" : "10") + " " + edition).TrimEnd();
        }
        else if (windows11 && name.StartsWith("Windows 10", StringComparison.Ordinal))
        {
            name = "Windows 11" + name["Windows 10".Length..];
        }
        return name;
    }
}

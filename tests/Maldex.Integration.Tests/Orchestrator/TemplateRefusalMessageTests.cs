using Maldex.Orchestrator.Templates;
using Xunit;

namespace Maldex.Integration.Tests.Orchestrator;

/// <summary>
/// Pins the refusal messages the UI recognises by prefix (ui/src/pages/TemplateJobPage.tsx, REFUSAL_PREFIXES).
/// A plain test: no fixture and no SQL Server. Keep the strings below in step with that list.
/// </summary>
public class TemplateRefusalMessageTests
{
    // Same two strings as REFUSAL_PREFIXES in ui/src/pages/TemplateJobPage.tsx.
    private static readonly string[] UiRefusalPrefixes =
    [
        "Defender real-time protection is still on",
        "Defender state could not be confirmed",
    ];

    [Fact]
    public void Refusal_messages_start_with_the_prefixes_the_UI_matches()
    {
        Assert.StartsWith(UiRefusalPrefixes[0], TemplateCoordinator.DefenderStillOn, StringComparison.Ordinal);
        Assert.StartsWith(UiRefusalPrefixes[1], TemplateCoordinator.DefenderUnconfirmed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Configuring_guidance_is_not_a_refusal()
    {
        Assert.All(UiRefusalPrefixes, p => Assert.False(TemplateCoordinator.DefenderGuidance.StartsWith(p, StringComparison.Ordinal)));
    }
}

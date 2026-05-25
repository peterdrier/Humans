using AwesomeAssertions;
using Humans.Application.Services.Shifts;

namespace Humans.Application.Tests.Services.Shifts;

public sealed class TeamPaletteTests
{
    [HumansFact]
    public void ColorFor_SameId_ReturnsSameColor()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        TeamPalette.ColorFor(id).Should().Be(TeamPalette.ColorFor(id));
    }

    [HumansFact]
    public void ColorFor_DifferentIds_OftenDifferent()
    {
        // Across 20 different Guids, we expect at least 5 distinct colors out of 20 palette entries.
        var colors = Enumerable.Range(0, 20)
            .Select(i => Guid.Parse($"{i:D8}-0000-0000-0000-000000000000"))
            .Select(TeamPalette.ColorFor)
            .Distinct(StringComparer.Ordinal)
            .Count();
        colors.Should().BeGreaterThan(5);
    }

    [HumansFact]
    public void ColorFor_ReturnsSixDigitHexWithLeadingHash()
    {
        var color = TeamPalette.ColorFor(Guid.NewGuid());
        color.Should().MatchRegex("^#[0-9A-Fa-f]{6}$");
    }

    [HumansFact]
    public void ColorFor_StabilityAcrossGuidFormatting_IsLocked()
    {
        // Spec locks Guid.ToString("D") — this test catches future drift.
        var id = Guid.Parse("abcd1234-5678-9abc-def0-123456789abc");
        TeamPalette.ColorFor(id).Should().Be("#" + ExpectedHexForGuidD(id));

        // Helper to make the lock explicit. If you change the hash algorithm,
        // update this expected value AND document why in the commit.
        static string ExpectedHexForGuidD(Guid id)
        {
            // Pre-computed expected color for this specific Guid + the documented palette.
            // Captured from the impl on 2026-05-24; if the hash or palette changes this
            // must be regenerated and the change explained in the commit.
            _ = id;
            return "637939";
        }
    }
}

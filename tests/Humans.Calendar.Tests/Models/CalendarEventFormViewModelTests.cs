using AwesomeAssertions;
using Humans.Calendar.Models;
using Xunit;

namespace Humans.Calendar.Tests.Models;

/// <summary>
/// The form's timezone box is free text, and the controller needs a zone object out of it to
/// convert the posted local times — so unlike <c>CalendarService.ValidateTimezone</c>, which
/// treats a blank timezone as valid (a non-recurring event stores none), blank is unusable here.
/// </summary>
public sealed class CalendarEventFormViewModelTests
{
    // Model binding turns a cleared box into null, and NodaTime's GetZoneOrNull throws on null
    // rather than returning it — so before this guard, clearing the field and submitting Create
    // or Edit answered 500. Reproduced against the PR preview at 1578.n.burn.camp.
    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolveZone_ReturnsNull_ForABlankTimezone(string? tz)
    {
        CalendarEventFormViewModel.TryResolveZone(tz).Should().BeNull();
    }

    [HumansFact]
    public void TryResolveZone_ReturnsNull_ForAnUnknownZone()
    {
        CalendarEventFormViewModel.TryResolveZone("Mars/Olympus_Mons").Should().BeNull();
    }

    [HumansFact]
    public void TryResolveZone_ResolvesAKnownZone()
    {
        CalendarEventFormViewModel.TryResolveZone("Europe/Madrid")!.Id.Should().Be("Europe/Madrid");
    }
}

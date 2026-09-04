using AwesomeAssertions;
using Humans.Calendar.Models;
using Humans.Calendar.Services.Dtos;
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

    // Both directions have shipped wrong on this branch: mapping null to RecurrenceRule put a
    // Title error under the RRULE input, and then mapping every non-timezone member to the form
    // level took the genuine RRULE error off its own field.
    [HumansTheory]
    [InlineData(nameof(CreateCalendarEventDto.RecurrenceTimezone), nameof(CalendarEventFormViewModel.RecurrenceTimezone))]
    [InlineData(nameof(CreateCalendarEventDto.RecurrenceRule), nameof(CalendarEventFormViewModel.RecurrenceRule))]
    public void ErrorFieldFor_KeepsAServiceNamedMemberOnItsOwnField(string serviceMember, string expected)
    {
        CalendarEventFormViewModel.ErrorFieldFor(serviceMember).Should().Be(expected);
    }

    // Title, EndUtc and start-after-end reach the caller through Failed(), which names no
    // member. Those belong to the validation summary, not to a field that did not cause them.
    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Title")]
    public void ErrorFieldFor_SendsAnUnnamedMemberToTheFormLevel(string? serviceMember)
    {
        CalendarEventFormViewModel.ErrorFieldFor(serviceMember).Should().BeEmpty();
    }
}

using AwesomeAssertions;
using Humans.Calendar.Models;
using NodaTime;
using NodaTime.Text;
using Xunit;

namespace Humans.Calendar.Tests.Models;

/// <summary>
/// A hand-typed or truncated <c>{originalStartUtc}</c> route segment used to throw out of
/// <c>InstantPattern.ExtendedIso.Parse</c>, turning a missing occurrence into a 500 instead
/// of a 404. <c>TryParseOriginal</c> replaced the throwing parse with null.
/// </summary>
public sealed class OccurrenceOverrideFormViewModelTests
{
    [HumansFact]
    public void DateOverride_ParsesDatesWithoutTimezoneAndRejectsPostedTimes()
    {
        OccurrenceOverrideFormViewModel.TryParseOriginalDate("2026-03-29").Should().Be(new LocalDate(2026, 3, 29));
        OccurrenceOverrideFormViewModel.TryParseOriginalDate("2026-03-29T14:00:00Z").Should().BeNull();
        var form = new OccurrenceOverrideFormViewModel
        {
            OverrideStartDateLocal = new DateTime(2026, 3, 29),
            OverrideEndDateLocal = new DateTime(2026, 3, 29),
        };
        form.TryBuildOverride(DateTimeZoneProviders.Tzdb["America/Los_Angeles"], out var dto).Should().BeTrue();
        dto.OverrideStartDate.Should().Be(new LocalDate(2026, 3, 29));
        dto.OverrideEndDateExclusive.Should().Be(new LocalDate(2026, 3, 30));
        dto.OverrideStartUtc.Should().BeNull();
        form.OverrideStartDateLocal = new DateTime(2026, 3, 29, 14, 0, 0);
        form.TryBuildOverride(DateTimeZone.Utc, out _).Should().BeFalse();
    }

    [HumansTheory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("2024-01-01T00:00:0")]
    public void TryParseOriginal_ReturnsNull_ForAnUnparsableSegment(string s)
    {
        OccurrenceOverrideFormViewModel.TryParseOriginal(s).Should().BeNull();
    }

    [HumansFact]
    public void TryParseOriginal_RoundTripsAValidInstant()
    {
        var instant = Instant.FromUtc(2024, 1, 1, 0, 0, 0);
        var text = InstantPattern.ExtendedIso.Format(instant);

        OccurrenceOverrideFormViewModel.TryParseOriginal(text).Should().Be(instant);
    }
}

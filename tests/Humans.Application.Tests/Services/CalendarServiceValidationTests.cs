using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Humans.Application.Services.Calendar;
using Humans.Testing;
using NodaTime;
using NodaTime.TimeZones;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Unit tests for the write-time validation helpers that support PR #562.
///
/// <para>Covers:</para>
/// <list type="bullet">
///   <item><see cref="CalendarService.ValidateRecurrenceRule"/> — rejects malformed
///     RRULEs so they cannot persist and blow up later occurrence expansion.</item>
///   <item>NodaTime Tzdb contract — <c>GetZoneOrNull</c> returns null for unknown IDs
///     (which is what the CalendarController timezone guard depends on) and the indexer
///     throws the way the original bug reported.</item>
/// </list>
/// </summary>
public class CalendarServiceValidationTests
{
    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("FREQ=DAILY")]
    [InlineData("FREQ=WEEKLY;BYDAY=TU;COUNT=4")]
    [InlineData("FREQ=WEEKLY;UNTIL=20240201T000000Z")]
    public void ValidateRecurrenceRule_valid_input_does_not_throw(string? rrule)
    {
        var act = () => CalendarService.ValidateRecurrenceRule(rrule);
        act.Should().NotThrow();
    }

    [HumansTheory]
    [InlineData("FREQ=NOT_A_REAL_FREQ")]
    [InlineData("FREQ=WEEKLY;BYDAY=XX")]
    public void ValidateRecurrenceRule_malformed_input_throws_ValidationException(string rrule)
    {
        var act = () => CalendarService.ValidateRecurrenceRule(rrule);
        act.Should().Throw<ValidationException>()
            .WithMessage("*Recurrence rule is malformed*");
    }

    // The three tests below document the NodaTime Tzdb contract the CalendarController
    // timezone guard depends on — GetZoneOrNull returns null for unknown IDs, while the
    // indexer throws (which was the original #562 bug). Pin the contract so a NodaTime
    // upgrade that changes either behavior is caught at test time.

    [HumansTheory]
    [InlineData("Europe/Madrid")]
    [InlineData("UTC")]
    [InlineData("America/Los_Angeles")]
    public void Tzdb_GetZoneOrNull_returns_zone_for_known_id(string id)
    {
        DateTimeZoneProviders.Tzdb.GetZoneOrNull(id).Should().NotBeNull();
    }

    [HumansTheory]
    [InlineData("Europe/Madird")]       // typo'd Madrid, original bug example
    [InlineData("Not/A/Real/Zone")]
    [InlineData("")]
    public void Tzdb_GetZoneOrNull_returns_null_for_unknown_id(string id)
    {
        DateTimeZoneProviders.Tzdb.GetZoneOrNull(id).Should().BeNull();
    }

    [HumansFact]
    public void Tzdb_indexer_throws_for_unknown_id()
    {
        // The indexer is what the pre-fix controller used; it throws, which surfaced
        // as a 500 to the user on submit. The fix swaps this for GetZoneOrNull.
        var act = () => DateTimeZoneProviders.Tzdb["Europe/Madird"];
        act.Should().Throw<DateTimeZoneNotFoundException>();
    }
}

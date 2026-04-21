using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Humans.Application.Services.Calendar;
using Humans.Testing;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Unit tests for <see cref="CalendarService.ValidateRecurrenceRule"/>. Verifies the
/// write-time guard that stops malformed RRULEs from persisting and breaking later
/// occurrence expansion in calendar reads.
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
}

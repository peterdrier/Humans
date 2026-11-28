using AwesomeAssertions;
using NodaTime;
using Humans.Calendar.Domain;

namespace Humans.Calendar.Tests.Domain;

public class CalendarEventExceptionTests
{
    private static readonly Instant When = Instant.FromUtc(2026, 2, 10, 18, 0);

    [HumansFact]
    public void Exception_Empty_IsInvalid()
    {
        var ex = new CalendarEventException
        {
            OriginalOccurrenceStartUtc = When,
            IsCancelled = false
        };

        var errors = ex.Validate();

        errors.Should().NotBeEmpty();
    }

    [HumansFact]
    public void Exception_Cancelled_IsValid()
    {
        var ex = new CalendarEventException
        {
            OriginalOccurrenceStartUtc = When,
            IsCancelled = true
        };

        var errors = ex.Validate();

        errors.Should().BeEmpty();
    }

    [HumansFact]
    public void Exception_Override_IsValid()
    {
        var ex = new CalendarEventException
        {
            OriginalOccurrenceStartUtc = When,
            IsCancelled = false,
            OverrideTitle = "Moved!"
        };

        var errors = ex.Validate();

        errors.Should().BeEmpty();
    }
}

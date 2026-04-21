using AwesomeAssertions;
using NodaTime;
using Humans.Domain.Entities;
using Xunit;

namespace Humans.Domain.Tests.Entities;

public class CalendarEventExceptionTests
{
    private static readonly Instant When = Instant.FromUtc(2026, 2, 10, 18, 0);

    [Fact]
    public void Empty_exception_is_invalid()
    {
        var ex = new CalendarEventException
        {
            OriginalOccurrenceStartUtc = When,
            IsCancelled = false
        };

        var errors = ex.Validate();

        errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Cancelled_exception_is_valid()
    {
        var ex = new CalendarEventException
        {
            OriginalOccurrenceStartUtc = When,
            IsCancelled = true
        };

        var errors = ex.Validate();

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Override_exception_is_valid()
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

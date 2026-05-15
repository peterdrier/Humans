using AwesomeAssertions;
using Humans.Application.Interfaces.Tickets.Dtos;
using NodaTime;

namespace Humans.Application.Tests.Services.Tickets;

public class AttendeeImportResultTests
{
    [HumansFact]
    public void FormatSummary_IncludesAllCounters()
    {
        var result = new AttendeeImportResult(
            TotalAttempted: 100,
            UsersCreated: 60,
            AttachedToExistingVerified: 30,
            UnverifiedRowsDeletedAndUserCreated: 5,
            AmbiguousSkipped: 2,
            NoEmailSkipped: 3,
            VanishedBetweenPlanAndApply: 1,
            Errors: 0,
            Elapsed: Duration.FromSeconds(12));

        var summary = result.FormatSummary();

        summary.Should().Contain("created=60");
        summary.Should().Contain("attached=30");
        summary.Should().Contain("unverified-replaced=5");
        summary.Should().Contain("ambiguous=2");
        summary.Should().Contain("no-email=3");
        summary.Should().Contain("vanished=1");
        summary.Should().Contain("errors=0");
        summary.Should().Contain("12000ms");
    }
}

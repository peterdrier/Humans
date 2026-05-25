using NodaTime;

namespace Humans.Application.Interfaces.Tickets.Dtos;

/// <summary>
/// Outcome counters from
/// <see cref="Humans.Application.Interfaces.Tickets.IAttendeeContactImportService.ApplyAsync"/>.
/// Format mirrors the Mailer import summary so admin banner / audit row
/// read consistently across import jobs.
/// </summary>
public sealed record AttendeeImportResult(
    int TotalAttempted,
    int UsersCreated,
    int AttachedToExistingVerified,
    int UnverifiedRowsDeletedAndUserCreated,
    int AmbiguousSkipped,
    int NoEmailSkipped,
    int VanishedBetweenPlanAndApply,
    int Errors,
    Duration Elapsed)
{
    public string FormatSummary() =>
        $"attempted={TotalAttempted}, created={UsersCreated}, attached={AttachedToExistingVerified}, " +
        $"unverified-replaced={UnverifiedRowsDeletedAndUserCreated}, ambiguous={AmbiguousSkipped}, " +
        $"no-email={NoEmailSkipped}, vanished={VanishedBetweenPlanAndApply}, errors={Errors}, " +
        $"elapsed={(long)Elapsed.TotalMilliseconds}ms";
}

using NodaTime;

namespace Humans.Application.Interfaces.Mailer.Dtos;

public sealed record ImportResult(
    int TotalPulled,
    int ContactsCreated,
    int PrefsFlipped,
    int PrefsPreservedByConflict,
    int UnverifiedRowsDeletedAndSuperseded,
    int AmbiguousSkipped,
    int UnconfirmedSkipped,
    int VanishedBetweenPlanAndApply,
    int Errors,
    Duration Elapsed)
{
    public string FormatSummary() =>
        $"MailerLite reconciliation: {TotalPulled} pulled, " +
        $"{ContactsCreated} contacts created, " +
        $"{PrefsFlipped} prefs updated, {PrefsPreservedByConflict} prefs preserved by conflict-rule, " +
        $"{UnverifiedRowsDeletedAndSuperseded} unverified rows deleted-and-superseded, " +
        $"{AmbiguousSkipped} ambiguous skipped, " +
        $"{UnconfirmedSkipped} unconfirmed skipped, " +
        $"{VanishedBetweenPlanAndApply} vanished, " +
        $"{Elapsed.TotalSeconds:F1}s elapsed, {Errors} errors.";
}

using NodaTime;

namespace Humans.Application.Interfaces.Mailer.Dtos;

public sealed record ImportResult(
    int TotalPulled,
    int HumansCreated,
    int PrefsFlippedToOptIn,
    int PrefsFlippedToOptOut,
    int PrefsKeptByConflict,
    int UnverifiedEmailsReplaced,
    int AmbiguousSkipped,
    int UnconfirmedSkipped,
    int VanishedBetweenPlanAndApply,
    int DecisionsThrottled,
    int Errors,
    Duration Elapsed)
{
    public string FormatSummary() =>
        $"MailerLite reconciliation: {TotalPulled} pulled, " +
        $"{HumansCreated} humans created, " +
        $"{PrefsFlippedToOptIn} flipped to opt-in, {PrefsFlippedToOptOut} flipped to opt-out, " +
        $"{PrefsKeptByConflict} kept by conflict-rule, " +
        $"{UnverifiedEmailsReplaced} unverified emails replaced, " +
        $"{AmbiguousSkipped} ambiguous skipped, " +
        $"{UnconfirmedSkipped} unconfirmed skipped, " +
        $"{VanishedBetweenPlanAndApply} vanished, " +
        $"{DecisionsThrottled} throttled, " +
        $"{Elapsed.TotalSeconds:F1}s elapsed, {Errors} errors.";
}

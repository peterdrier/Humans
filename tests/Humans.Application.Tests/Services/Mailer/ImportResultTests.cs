using AwesomeAssertions;
using Humans.Application.Interfaces.Mailer.Dtos;
using NodaTime;

namespace Humans.Application.Tests.Services.Mailer;

public class ImportResultTests
{
    private static ImportResult Sample(
        int totalPulled = 50,
        int humansCreated = 3,
        int prefsFlippedToOptIn = 2,
        int prefsFlippedToOptOut = 1,
        int prefsKeptByConflict = 1,
        int unverifiedEmailsReplaced = 0,
        int ambiguousSkipped = 0,
        int unconfirmedSkipped = 5,
        int vanishedBetweenPlanAndApply = 1,
        int decisionsThrottled = 0,
        int errors = 0,
        Duration? elapsed = null) =>
        new(
            TotalPulled: totalPulled,
            HumansCreated: humansCreated,
            PrefsFlippedToOptIn: prefsFlippedToOptIn,
            PrefsFlippedToOptOut: prefsFlippedToOptOut,
            PrefsKeptByConflict: prefsKeptByConflict,
            UnverifiedEmailsReplaced: unverifiedEmailsReplaced,
            AmbiguousSkipped: ambiguousSkipped,
            UnconfirmedSkipped: unconfirmedSkipped,
            VanishedBetweenPlanAndApply: vanishedBetweenPlanAndApply,
            DecisionsThrottled: decisionsThrottled,
            Errors: errors,
            Elapsed: elapsed ?? Duration.FromSeconds(7));

    [HumansFact]
    public void FormatSummary_StartsWithExpectedPrefix()
    {
        var result = Sample();
        result.FormatSummary().Should().StartWith("MailerLite reconciliation:");
    }

    [HumansFact]
    public void FormatSummary_IncludesAllKeyCounters()
    {
        var result = Sample(
            totalPulled: 50,
            humansCreated: 3,
            prefsFlippedToOptIn: 2,
            prefsFlippedToOptOut: 1,
            unconfirmedSkipped: 5,
            vanishedBetweenPlanAndApply: 1,
            decisionsThrottled: 4,
            errors: 0,
            elapsed: Duration.FromSeconds(7));

        var summary = result.FormatSummary();

        summary.Should().Contain("50 pulled");
        summary.Should().Contain("3 humans created");
        summary.Should().Contain("2 flipped to opt-in");
        summary.Should().Contain("1 flipped to opt-out");
        summary.Should().Contain("5 unconfirmed skipped");
        summary.Should().Contain("1 vanished");
        summary.Should().Contain("4 throttled");
        summary.Should().Contain("7.0s elapsed");
        summary.Should().Contain("0 errors");
    }

    [HumansFact]
    public void FormatSummary_ContainsNoPii()
    {
        var result = Sample();
        result.FormatSummary().Should().NotContain("@");
    }
}

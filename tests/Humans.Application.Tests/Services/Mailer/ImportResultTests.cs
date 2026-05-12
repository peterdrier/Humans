using AwesomeAssertions;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Testing;
using NodaTime;

namespace Humans.Application.Tests.Services.Mailer;

public class ImportResultTests
{
    private static ImportResult Sample(
        int totalPulled = 50,
        int contactsCreated = 3,
        int prefsFlipped = 2,
        int prefsPreservedByConflict = 1,
        int unverifiedRowsDeletedAndSuperseded = 0,
        int ambiguousSkipped = 0,
        int unconfirmedSkipped = 5,
        int vanishedBetweenPlanAndApply = 1,
        int errors = 0,
        Duration? elapsed = null) =>
        new(
            TotalPulled: totalPulled,
            ContactsCreated: contactsCreated,
            PrefsFlipped: prefsFlipped,
            PrefsPreservedByConflict: prefsPreservedByConflict,
            UnverifiedRowsDeletedAndSuperseded: unverifiedRowsDeletedAndSuperseded,
            AmbiguousSkipped: ambiguousSkipped,
            UnconfirmedSkipped: unconfirmedSkipped,
            VanishedBetweenPlanAndApply: vanishedBetweenPlanAndApply,
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
            contactsCreated: 3,
            prefsFlipped: 2,
            unconfirmedSkipped: 5,
            vanishedBetweenPlanAndApply: 1,
            errors: 0,
            elapsed: Duration.FromSeconds(7));

        var summary = result.FormatSummary();

        summary.Should().Contain("50 pulled");
        summary.Should().Contain("3 contacts created");
        summary.Should().Contain("2 prefs updated");
        summary.Should().Contain("5 unconfirmed skipped");
        summary.Should().Contain("1 vanished");
        summary.Should().Contain("7.0s elapsed");
        summary.Should().Contain("0 errors");
    }

    [HumansFact]
    public void FormatSummary_ContainsNoPii()
    {
        // Confirm the summary is purely numeric/label — no email addresses or personal data.
        var result = Sample();
        result.FormatSummary().Should().NotContain("@");
    }
}

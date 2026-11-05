using AwesomeAssertions;
using Humans.Stripe.Domain;
using NodaTime;

namespace Humans.Stripe.Tests.Mirror;

public sealed class StripeLedgerTests
{
    private static int _next;

    internal static StripeBalanceTransaction Row(long amount, LocalDate availableOn, string category = StripeLedger.RiskReserve,
        LocalDate? created = null) => new()
        {
            Id = $"txn_{++_next}",
            AccountId = "acct_t",
            Type = string.Equals(category, StripeLedger.RiskReserve, StringComparison.Ordinal) ? "reserved_funds" : category,
            ReportingCategory = category,
            Currency = "EUR",
            Amount = amount,
            Net = amount,
            Created = (created ?? availableOn).AtMidnight().InUtc().ToInstant(),
            AvailableOn = availableOn.AtMidnight().InUtc().ToInstant(),
        };

    [HumansFact]
    public void Held_follows_available_dates_not_creation()
    {
        // Two charges' holds and releases, each pair created together on the charge's day.
        var rows = new[]
        {
            Row(-100_00, new LocalDate(2026, 7, 6), created: new LocalDate(2026, 7, 4)),
            Row(100_00, new LocalDate(2026, 7, 20), created: new LocalDate(2026, 7, 4)),
            Row(-50_00, new LocalDate(2026, 7, 9), created: new LocalDate(2026, 7, 7)),
            Row(50_00, new LocalDate(2026, 7, 20), created: new LocalDate(2026, 7, 7)),
        };

        var curve = StripeLedger.HeldOverTime(rows);

        curve.Should().Equal(
            new HeldPoint(new LocalDate(2026, 7, 6), 100_00),
            new HeldPoint(new LocalDate(2026, 7, 9), 150_00),
            new HeldPoint(new LocalDate(2026, 7, 20), 0));
        StripeLedger.HeldOn(curve, new LocalDate(2026, 7, 5)).Should().Be(0);
        StripeLedger.HeldOn(curve, new LocalDate(2026, 7, 15)).Should().Be(150_00);
        StripeLedger.HeldOn(curve, new LocalDate(2026, 11, 5)).Should().Be(0);
    }

    [HumansFact]
    public void An_early_release_lowers_the_hold_from_its_own_date()
    {
        var rows = new[]
        {
            Row(-125_000_00, new LocalDate(2026, 4, 1)),
            Row(125_000_00, new LocalDate(2026, 7, 20)),
            // Early release now, offset by cancelling that much of the scheduled release.
            Row(75_000_00, new LocalDate(2026, 4, 7)),
            Row(-75_000_00, new LocalDate(2026, 7, 20)),
        };

        var curve = StripeLedger.HeldOverTime(rows);

        StripeLedger.HeldOn(curve, new LocalDate(2026, 4, 3)).Should().Be(125_000_00);
        StripeLedger.HeldOn(curve, new LocalDate(2026, 5, 1)).Should().Be(50_000_00);
        StripeLedger.HeldOn(curve, new LocalDate(2026, 7, 20)).Should().Be(0);
    }

    [HumansFact]
    public void A_late_stamped_available_on_lands_on_its_utc_date()
    {
        var row = Row(-1_00, new LocalDate(2026, 7, 9));
        row.AvailableOn = Instant.FromUtc(2026, 7, 9, 23, 30);

        StripeLedger.AvailableDate(row).Should().Be(new LocalDate(2026, 7, 9));
    }
}

using NodaTime;

namespace Humans.Stripe.Domain;

internal sealed record HeldPoint(LocalDate Date, long Held);

/// <summary>Pure reads over one account's balance transactions.</summary>
internal static class StripeLedger
{
    public const string RiskReserve = "risk_reserved_funds";
    public const string MinimumBalanceHold = "payout_minimum_balance_hold";

    public static bool IsRiskReserve(StripeBalanceTransaction t) =>
        string.Equals(t.ReportingCategory, RiskReserve, StringComparison.Ordinal);

    /// <summary>Stripe keeps a payout minimum back by a hold and a release in the same second around each payout: the
    /// hold leaves the amount out of the payout, the release returns it to the available balance.</summary>
    public static bool IsMinimumBalanceHold(StripeBalanceTransaction t) =>
        string.Equals(t.ReportingCategory, MinimumBalanceHold, StringComparison.Ordinal);

    /// <summary>Stripe stamps daily availability at 00:00 UTC, so the UTC date is the day it lands.</summary>
    public static LocalDate AvailableDate(StripeBalanceTransaction t) => t.AvailableOn.InUtc().Date;

    /// <summary>
    /// How much the rows hold back, day by day: the negated running sum of their amounts by available date. A hold and
    /// its release are created together, so the effect is by when each lands, not when it was made. One point per day
    /// the amount changes.
    /// </summary>
    public static IReadOnlyList<HeldPoint> HeldOverTime(IEnumerable<StripeBalanceTransaction> rows)
    {
        var points = new List<HeldPoint>();
        long held = 0;
        foreach (var day in rows.GroupBy(AvailableDate).OrderBy(g => g.Key))
        {
            var next = held - day.Sum(t => t.Amount);
            if (next != held || points.Count == 0) points.Add(new HeldPoint(day.Key, next));
            held = next;
        }
        return points;
    }

    /// <summary>Held at the end of <paramref name="date"/>: the last point on or before it.</summary>
    public static long HeldOn(IReadOnlyList<HeldPoint> curve, LocalDate date) =>
        curve.LastOrDefault(p => p.Date <= date)?.Held ?? 0;
}

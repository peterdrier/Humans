using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Services;

internal static class TermExpiryCalculator
{
    internal static LocalDate GetRenewalThreshold(LocalDate today) => today.PlusDays(90);

    internal static (LocalDate? ExpiresAt, bool ExpiresSoon, bool Expired, bool HasPendingApplication) ComputeTermState(
        IReadOnlyList<UserApplicationSnapshot> applications, MembershipTier currentTier, LocalDate today)
    {
        var hasPendingApplication = applications.MaxBy(a => a.SubmittedAt)?.Status == ApplicationStatus.Submitted;
        var latestApprovedApp = applications
            .Where(a => a.Status == ApplicationStatus.Approved
                && a.MembershipTier == currentTier
                && a.TermExpiresAt is not null)
            .OrderByDescending(a => a.TermExpiresAt)
            .FirstOrDefault();
        if (latestApprovedApp?.TermExpiresAt is null)
            return (null, false, false, hasPendingApplication);

        var expiryDate = latestApprovedApp.TermExpiresAt.Value;
        var expired = expiryDate < today;
        return (expiryDate, !expired && expiryDate <= GetRenewalThreshold(today), expired, hasPendingApplication);
    }

    /// <summary>
    /// Computes the term expiry date: Dec 31 of the current cycle's odd year.
    /// A term granted in 2026 or 2027 ends 2027-12-31; one granted in 2028 or 2029 ends 2029-12-31.
    /// Q4 of an odd year is the renewal window (reminders go out 90 days before expiry), so an
    /// approval from October of an odd year onward belongs to the next cycle: Nov 2027 -> 2029-12-31.
    /// </summary>
    public static LocalDate ComputeTermExpiry(LocalDate today)
    {
        var targetYear = today.Year % 2 == 0 ? today.Year + 1 : today.Year;
        if (today.Year % 2 == 1 && today.Month >= 10)
            targetYear += 2;
        return new LocalDate(targetYear, 12, 31);
    }
}

using NodaTime;

namespace Humans.Governance.Services;

/// <summary>
/// Computes term expiry dates for Colaborador/Asociado membership.
/// Terms are 2-year synchronized cycles expiring Dec 31 of odd years.
/// </summary>
internal static class TermExpiryCalculator
{
    /// <summary>
    /// Computes the term expiry date: Dec 31 of the current cycle's odd year.
    /// A term granted in 2026 or 2027 ends 2027-12-31; one granted in 2028 or 2029 ends 2029-12-31.
    /// </summary>
    public static LocalDate ComputeTermExpiry(LocalDate today)
    {
        var targetYear = today.Year % 2 == 0 ? today.Year + 1 : today.Year;
        return new LocalDate(targetYear, 12, 31);
    }
}

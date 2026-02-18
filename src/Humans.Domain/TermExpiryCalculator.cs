using NodaTime;

namespace Humans.Domain;

/// <summary>
/// Computes term expiry dates for Colaborador/Asociado membership.
/// Terms are 2-year synchronized cycles expiring Dec 31 of odd years.
/// </summary>
public static class TermExpiryCalculator
{
    /// <summary>
    /// Computes the term expiry date: the next Dec 31 of an odd year that is at least 2 years from the given date.
    /// </summary>
    public static LocalDate ComputeTermExpiry(LocalDate today)
    {
        var targetYear = today.Year + 2;
        if (targetYear % 2 == 0)
        {
            targetYear++;
        }
        return new LocalDate(targetYear, 12, 31);
    }
}

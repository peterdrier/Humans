using NodaTime;

namespace Humans.Finance.Contracts;

/// <summary>Shared VAT rules for budget summaries, category projections and cash flow.</summary>
public static class VatCalculation
{
    /// <summary>The VAT portion of a VAT-inclusive amount: |amount| × rate / (100 + rate).</summary>
    public static decimal ComputePortion(decimal amount, int vatRate) =>
        Math.Abs(amount) * vatRate / (100m + vatRate);

    /// <summary>Spanish quarterly VAT settles 45 days after the expected date's quarter-end.</summary>
    public static LocalDate ComputeSettlementDate(LocalDate expectedDate)
    {
        var quarterEnd = expectedDate.Month switch
        {
            >= 1 and <= 3 => new LocalDate(expectedDate.Year, 3, 31),
            >= 4 and <= 6 => new LocalDate(expectedDate.Year, 6, 30),
            >= 7 and <= 9 => new LocalDate(expectedDate.Year, 9, 30),
            _ => new LocalDate(expectedDate.Year, 12, 31)
        };
        return quarterEnd.PlusDays(45);
    }
}

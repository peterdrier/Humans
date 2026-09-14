using AwesomeAssertions;
using Humans.Governance.Services;
using NodaTime;
using Xunit;

namespace Humans.Governance.Tests.Services;

public class TermExpiryCalculatorTests
{
    [HumansTheory]
    [InlineData(2026, 3, 15, 2027, 12, 31)]  // even year → end of the 2026/27 cycle
    [InlineData(2027, 6, 1, 2027, 12, 31)]   // odd year → end of that same year
    [InlineData(2027, 9, 30, 2027, 12, 31)]  // last day before the renewal window
    [InlineData(2027, 10, 1, 2029, 12, 31)]  // Q4 of an odd year is the renewal window → next cycle
    [InlineData(2027, 12, 31, 2029, 12, 31)]
    [InlineData(2028, 1, 1, 2029, 12, 31)]   // even year → end of the 2028/29 cycle
    [InlineData(2028, 11, 1, 2029, 12, 31)]  // Q4 of an even year is not a renewal window
    [InlineData(2029, 7, 15, 2029, 12, 31)]  // odd year → end of that same year
    public void ComputeTermExpiry_ReturnsDec31OfCurrentCyclesOddYear(
        int year, int month, int day,
        int expectedYear, int expectedMonth, int expectedDay)
    {
        var today = new LocalDate(year, month, day);

        var result = TermExpiryCalculator.ComputeTermExpiry(today);

        result.Should().Be(new LocalDate(expectedYear, expectedMonth, expectedDay));
    }

    [HumansFact]
    public void ComputeTermExpiry_AlwaysReturnsDec31()
    {
        var today = new LocalDate(2026, 6, 15);

        var result = TermExpiryCalculator.ComputeTermExpiry(today);

        result.Month.Should().Be(12);
        result.Day.Should().Be(31);
    }

    [HumansFact]
    public void ComputeTermExpiry_AlwaysReturnsOddYear()
    {
        for (var year = 2024; year <= 2035; year++)
        {
            var today = new LocalDate(year, 1, 1);
            var result = TermExpiryCalculator.ComputeTermExpiry(today);
            (result.Year % 2).Should().Be(1, $"year {year} should produce odd target year but got {result.Year}");
        }
    }
}

using AwesomeAssertions;
using NodaTime;
using Humans.Domain;
using Xunit;

namespace Humans.Domain.Tests;

public class TermExpiryCalculatorTests
{
    [Theory]
    [InlineData(2026, 3, 15, 2029, 12, 31)]  // 2026 + 2 = 2028 (even) → 2029
    [InlineData(2027, 6, 1, 2029, 12, 31)]   // 2027 + 2 = 2029 (odd) → 2029
    [InlineData(2028, 1, 1, 2031, 12, 31)]   // 2028 + 2 = 2030 (even) → 2031
    [InlineData(2025, 12, 31, 2027, 12, 31)] // 2025 + 2 = 2027 (odd) → 2027
    [InlineData(2029, 7, 15, 2031, 12, 31)]  // 2029 + 2 = 2031 (odd) → 2031
    public void ComputeTermExpiry_ReturnsNextOddYearDec31_AtLeast2YearsAway(
        int year, int month, int day,
        int expectedYear, int expectedMonth, int expectedDay)
    {
        var today = new LocalDate(year, month, day);

        var result = TermExpiryCalculator.ComputeTermExpiry(today);

        result.Should().Be(new LocalDate(expectedYear, expectedMonth, expectedDay));
    }

    [Fact]
    public void ComputeTermExpiry_AlwaysReturnsDec31()
    {
        var today = new LocalDate(2026, 6, 15);

        var result = TermExpiryCalculator.ComputeTermExpiry(today);

        result.Month.Should().Be(12);
        result.Day.Should().Be(31);
    }

    [Fact]
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

using System.Globalization;
using AwesomeAssertions;
using Humans.Base.Extensions;

namespace Humans.Base.Tests.Extensions;

public sealed class CurrencyFormattingExtensionsTests
{
    [HumansTheory]
    [Xunit.InlineData("en", "1,234.56 €", "1,235 €")]
    [Xunit.InlineData("es", "1.234,56 €", "1.235 €")]
    [Xunit.InlineData("de", "1.234,56 €", "1.235 €")]
    [Xunit.InlineData("it", "1.234,56 €", "1.235 €")]
    [Xunit.InlineData("fr", "1\u202f234,56 €", "1\u202f235 €")]
    [Xunit.InlineData("ca", "1.234,56 €", "1.235 €")]
    public void Euro_display_uses_ui_culture_while_parsing_stays_english(
        string culture, string expected, string expectedWhole)
    {
        using var scope = new CultureScope(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
        const decimal amount = 1234.56m;

        amount.ToEuro().Should().Be(expected);
        ((decimal?)amount).ToEuro().Should().Be(expected);
        ((decimal?)null).ToEuro().Should().BeEmpty();
        amount.ToEuro(0).Should().Be(expectedWhole);
        amount.ToSignedEuro().Should().Be($"+{expected}");
        (-amount).ToSignedEuro().Should().Be($"-{expected}");
        amount.ToString("0.00", CultureInfo.CurrentCulture).Should().Be("1234.56");
        decimal.Parse("1234.56", CultureInfo.CurrentCulture).Should().Be(amount);
        CultureInfo.CurrentCulture.Name.Should().Be("en");
        CultureInfo.CurrentUICulture.Name.Should().Be(culture);
    }
}

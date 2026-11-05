using System.Globalization;
using AwesomeAssertions;
using Humans.Base.Extensions;

namespace Humans.Base.Tests.Extensions;

/// <summary>
/// The one place euro formatting is tested per culture; section tests assert their own amounts in a single culture.
/// </summary>
public sealed class CurrencyFormattingExtensionsTests
{
    [HumansTheory]
    [Xunit.MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public void Euro_display_uses_ui_culture_while_parsing_stays_english(string culture)
    {
        using var scope = new CultureScope(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
        var ui = CultureInfo.CurrentUICulture;
        const decimal amount = 1234.56m;
        var expected = amount.ToString("N2", ui) + " €";

        amount.ToEuro().Should().Be(expected);
        ((decimal?)amount).ToEuro().Should().Be(expected);
        ((decimal?)null).ToEuro().Should().BeEmpty();
        amount.ToEuro(0).Should().Be(amount.ToString("N0", ui) + " €");
        amount.ToSignedEuro().Should().Be($"+{expected}");
        (-amount).ToSignedEuro().Should().Be($"-{expected}");
        amount.ToString("0.00", CultureInfo.CurrentCulture).Should().Be("1234.56");
        decimal.Parse("1234.56", CultureInfo.CurrentCulture).Should().Be(amount);
        CultureInfo.CurrentCulture.Name.Should().Be("en");
        CultureInfo.CurrentUICulture.Name.Should().Be(culture);
    }

    /// <summary>Pins the shape (grouping, decimals, trailing symbol) in a dot- and a comma-decimal culture.</summary>
    [HumansTheory]
    [Xunit.InlineData(CultureCatalog.DefaultCultureCode, "1,234.56 €", "1,235 €")]
    [Xunit.InlineData(CultureCatalog.CanonicalLegalCultureCode, "1.234,56 €", "1.235 €")]
    public void Euro_display_trails_the_symbol_after_the_grouped_amount(
        string culture, string expected, string expectedWhole)
    {
        using var scope = new CultureScope(culture);

        1234.56m.ToEuro().Should().Be(expected);
        1234.56m.ToEuro(0).Should().Be(expectedWhole);
    }
}

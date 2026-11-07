using AwesomeAssertions;
using Humans.Base.Helpers;
using Xunit;

namespace Humans.Base.Tests.Helpers;

public class IbanValidatorTests
{
    [HumansTheory]
    [InlineData("ES9121000418450200051332")]
    [InlineData("DE89370400440532013000")]
    [InlineData("NL91ABNA0417164300")]
    [InlineData("FR1420041010050500013M02606")]
    [InlineData("AL47212110090000000235698741")]
    [InlineData("MD24AG000225100013104168")]
    [InlineData("ME25505000012345678951")]
    [InlineData("MK07250120000058984")]
    [InlineData("VA59001123000012345678")]
    public void IsValid_AcceptsRealIbans(string iban)
    {
        IbanValidator.IsValid(iban).Should().BeTrue();
    }

    [HumansTheory]
    [InlineData("ES9121000418450200051333")]
    [InlineData("ES912100041845")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ES91 2100 0418 45")]
    [InlineData("ESAA00000000000000000060")]
    [InlineData("FR14É0000000000000000000085")]
    [InlineData("ES91٢1000418450200051332")]
    public void IsValid_RejectsBadInputs(string? iban)
    {
        IbanValidator.IsValid(iban).Should().BeFalse();
    }

    [HumansFact]
    public void IsValid_RejectsUnknownCountryPrefix()
    {
        IbanValidator.IsValid("XX9121000418450200051332").Should().BeFalse();
    }

    [HumansFact]
    public void IsValid_StripsSpacesBeforeChecking()
    {
        IbanValidator.IsValid("ES91 2100 0418 4502 0005 1332").Should().BeTrue();
    }

    [HumansFact]
    public void Normalize_ReturnsUppercaseNoSpaces()
    {
        IbanValidator.Normalize("es91 2100 0418 4502 0005 1332")
            .Should().Be("ES9121000418450200051332");
    }

    [HumansTheory]
    [InlineData(" ")]
    [InlineData("\u202f")]
    [InlineData("\u00a0")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void CopiedIban_WhitespaceDoesNotChangeItsIdentityOrChecksum(string separator)
    {
        var formatted = separator + string.Join(separator, "es91", "2100", "0418", "4502", "0005", "1332") + separator;

        IbanValidator.Normalize(formatted).Should().Be("ES9121000418450200051332");
        IbanValidator.IsValid(formatted).Should().BeTrue();
        IbanValidator.IsValid(formatted.Replace("1332", "1333", StringComparison.Ordinal)).Should().BeFalse();
        IbanValidator.CurrencyOf(formatted).Should().Be("EUR");
        IbanValidator.IsEea(formatted).Should().BeTrue();
    }

    [HumansTheory]
    [InlineData("ES9121000418450200051332", "EUR")]
    [InlineData("GB29****819", "GBP")]
    [InlineData("XX00****000", null)]
    [InlineData(null, null)]
    public void CurrencyOf_ReadsTheCountryEvenWhenMasked(string? iban, string? expected)
    {
        IbanValidator.CurrencyOf(iban).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData("SE45****000", true)]
    [InlineData("GB29****819", false)]
    [InlineData("XX00****000", null)]
    public void IsEea_ReadsTheCountryEvenWhenMasked(string iban, bool? expected)
    {
        IbanValidator.IsEea(iban).Should().Be(expected);
    }
}

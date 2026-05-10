using AwesomeAssertions;
using Humans.Domain.Helpers;
using Xunit;

namespace Humans.Domain.Tests.Helpers;

public class IbanValidatorTests
{
    [HumansTheory]
    [InlineData("ES9121000418450200051332")]
    [InlineData("DE89370400440532013000")]
    [InlineData("NL91ABNA0417164300")]
    [InlineData("FR1420041010050500013M02606")]
    public void IsValid_AcceptsRealIbans(string iban)
    {
        IbanValidator.IsValid(iban).Should().BeTrue();
    }

    [HumansTheory]
    [InlineData("ES9121000418450200051333")]
    [InlineData("ES912100041845")]
    [InlineData("XX9121000418450200051332")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ES91 2100 0418 45")]
    public void IsValid_RejectsBadInputs(string? iban)
    {
        IbanValidator.IsValid(iban).Should().BeFalse();
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

    [HumansFact]
    public void IsValid_StripsNarrowNoBreakSpace()
    {
        IbanValidator.IsValid("ES91 2100 0418 4502 0005 1332").Should().BeTrue();
    }
}

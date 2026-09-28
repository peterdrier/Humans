using AwesomeAssertions;
using Humans.Base.Helpers;
using Xunit;

namespace Humans.Base.Tests.Helpers;

public class IbanFormatterTests
{
    [HumansFact]
    public void Mask_ReturnsFirst4PlusStarsPlusLast3()
    {
        IbanFormatter.Mask("NL75ABNA0123456789").Should().Be("NL75****789");
    }

    [HumansFact]
    public void Mask_HandlesShortIban()
    {
        IbanFormatter.Mask("ES1234567890").Should().Be("ES12****890");
    }

    [HumansFact]
    public void Mask_StripsSpacesBeforeMasking()
    {
        IbanFormatter.Mask("NL75 ABNA 0123 4567 89").Should().Be("NL75****789");
    }

    [HumansFact]
    public void Mask_NullReturnsEmpty()
    {
        IbanFormatter.Mask(null).Should().Be("");
    }

    [HumansFact]
    public void Mask_EmptyReturnsEmpty()
    {
        IbanFormatter.Mask("").Should().Be("");
    }

    [HumansFact]
    public void Mask_TooShortToMaskReturnsAllStars()
    {
        IbanFormatter.Mask("NL75AB").Should().Be("****");
    }

    [HumansFact]
    public void MaskAllIn_MasksAnIbanEchoedBackInsideAVendorErrorBody()
    {
        IbanFormatter.MaskAllIn("""{"status":0,"info":"invalid iban ES9121000418450200051332"}""")
            .Should().Be("""{"status":0,"info":"invalid iban ES91****332"}""");
    }

    [HumansFact]
    public void MaskAllIn_MasksEveryOccurrence()
    {
        IbanFormatter.MaskAllIn("NL75ABNA0123456789 and ES9121000418450200051332")
            .Should().Be("NL75****789 and ES91****332");
    }

    [HumansFact]
    public void MaskAllIn_LeavesNonIbanTokensAlone()
    {
        // A Holded doc id and the surrounding prose must survive — the message is the diagnostic.
        IbanFormatter.MaskAllIn("Holded 400 Bad Request: document 65f0a1b2c3d4e5f6a7b8c9d0 rejected")
            .Should().Be("Holded 400 Bad Request: document 65f0a1b2c3d4e5f6a7b8c9d0 rejected");
    }

    [HumansTheory]
    [InlineData("refund to ES91 2100 0418 4502 0005 1332 please", "refund to ES91****332 please")]
    [InlineData("refund to es91 2100 0418 4502 0005 1332", "refund to es91****332")]
    [InlineData("iban ES91-2100-0418-4502-0005-1332.", "iban ES91****332.")]
    [InlineData("iban ES91 2100 0418 4502 0005 1332", "iban ES91****332")]
    [InlineData("ref FV24 ES9121000418450200051332", "ref FV24 ES91****332")]
    [InlineData("ES9121000418450200051332 ES91 2100 0418 4502 0005 1332", "ES91****332 ES91****332")]
    public void MaskAllIn_MasksHumanFormattedIbans(string text, string expected)
    {
        IbanFormatter.MaskAllIn(text).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData("EU27 MEMBER STATES AGREED")]
    [InlineData("document ab12cdef0123456789abcdef rejected")]
    public void MaskAllIn_LeavesSpacedNonIbanTextAlone(string text)
    {
        IbanFormatter.MaskAllIn(text).Should().Be(text);
    }

    [HumansFact]
    public void MaskAllIn_NullReturnsEmpty()
    {
        IbanFormatter.MaskAllIn(null).Should().Be("");
    }

    [HumansFact]
    public void Mask_StripsNarrowNoBreakSpace()
    {
        IbanFormatter.Mask("NL75 ABNA 0123 4567 89").Should().Be("NL75****789");
    }
}

using AwesomeAssertions;
using Humans.Domain.Helpers;

namespace Humans.Interfaces.Tests.Helpers;

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

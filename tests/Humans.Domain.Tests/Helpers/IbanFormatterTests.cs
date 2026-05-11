using AwesomeAssertions;
using Humans.Domain.Helpers;

namespace Humans.Domain.Tests.Helpers;

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
    public void Mask_StripsNarrowNoBreakSpace()
    {
        IbanFormatter.Mask("NL75 ABNA 0123 4567 89").Should().Be("NL75****789");
    }
}

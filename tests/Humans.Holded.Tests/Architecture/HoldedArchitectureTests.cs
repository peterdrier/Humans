using AwesomeAssertions;
using Humans.Holded.Contracts;

namespace Humans.Holded.Tests.Architecture;

public class HoldedArchitectureTests
{
    [HumansFact]
    public void HoldedExceptions_AreClassified_TransientOrPermanent()
    {
        typeof(HoldedTransientException).Should().BeAssignableTo<HoldedApiException>();
        typeof(HoldedPermanentException).Should().BeAssignableTo<HoldedApiException>();
    }
}

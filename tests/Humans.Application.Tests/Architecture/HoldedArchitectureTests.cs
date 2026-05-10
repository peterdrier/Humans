using AwesomeAssertions;
using Humans.Application.Interfaces.Holded;

namespace Humans.Application.Tests.Architecture;

public class HoldedArchitectureTests
{
    [HumansFact]
    public void IHoldedClient_LivesIn_HoldedNamespace()
    {
        typeof(IHoldedClient).Namespace
            .Should().Be("Humans.Application.Interfaces.Holded");
    }

    [HumansFact]
    public void HumansApplication_HasNoEFCoreReference()
    {
        var asm = typeof(IHoldedClient).Assembly;
        asm.GetReferencedAssemblies()
            .Should().NotContain(a => a.Name == "Microsoft.EntityFrameworkCore",
                "Humans.Application must not depend on EF Core");
    }

    [HumansFact]
    public void HoldedExceptions_AreClassified_TransientOrPermanent()
    {
        typeof(HoldedTransientException).Should().BeAssignableTo<HoldedApiException>();
        typeof(HoldedPermanentException).Should().BeAssignableTo<HoldedApiException>();
    }
}

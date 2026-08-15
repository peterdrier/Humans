using AwesomeAssertions;
using Humans.Holded.Contracts;

namespace Humans.Application.Tests.Architecture;

public class HoldedArchitectureTests
{
    [HumansFact]
    public void IHoldedClient_LivesIn_HoldedContractsNamespace()
    {
        typeof(IHoldedClient).Namespace
            .Should().Be("Humans.Holded.Contracts");
    }

    [HumansFact]
    public void HumansApplication_HasNoEFCoreReference()
    {
        // Anchored on a type that stays in Humans.Application. It used to read
        // typeof(IHoldedClient).Assembly, which G5 lane 4b-2f moved to Humans.Holded.Contracts —
        // the assertion would have silently started measuring a different assembly.
        var asm = typeof(CacheKeys).Assembly;
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

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
        // typeof(IHoldedClient).Assembly, which G5 lane 4b-2f moved to Humans.Holded.Contracts,
        // then typeof(CacheKeys).Assembly, which G5 lane 3a-1 moved to Humans.Interfaces with
        // its namespace preserved — both moves would have silently retargeted this assertion
        // onto the wrong assembly instead of failing. DashboardService is a concrete
        // Humans.Application service with no scheduled move in phase 3.
        var asm = typeof(Humans.Application.Services.Dashboard.DashboardService).Assembly;
        asm.GetName().Name.Should().Be("Humans.Application",
            because: "an anchor whose type leaves this assembly would silently retarget this test onto the wrong assembly instead of failing");

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

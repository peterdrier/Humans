using AwesomeAssertions;
using Humans.Holded.Contracts;

namespace Humans.Holded.Tests.Architecture;

public class HoldedArchitectureTests
{
    // COVERAGE REDUCED (nobodies-collective/Humans#866, G5 lane 5c): HumansApplication_HasNoEFCoreReference
    // is gone. It asserted the hub held no EF Core reference; G5 lane 5c emptied Humans.Application of
    // every type, so the assertion had nothing left to measure. Base is not a successor anchor — it
    // took EF Core at lane 3a-2 with the AddSectionDbContext cluster. The rule that still holds is
    // per-section and lives in ApplicationServicesTakeNoDbContextRule.
    [HumansFact]
    public void HoldedExceptions_AreClassified_TransientOrPermanent()
    {
        typeof(HoldedTransientException).Should().BeAssignableTo<HoldedApiException>();
        typeof(HoldedPermanentException).Should().BeAssignableTo<HoldedApiException>();
    }

    /// <summary>The purchase-doc sync is Finance's to trigger (its own job and Sync now); Holded
    /// never calls back into Finance's writes.</summary>
    [HumansFact]
    public void Holded_TakesNoFinanceWriteDependency()
    {
        typeof(IHoldedService).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance))
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .Should().NotContain(typeof(Humans.Finance.Contracts.IHoldedFinanceWrite));
    }
}

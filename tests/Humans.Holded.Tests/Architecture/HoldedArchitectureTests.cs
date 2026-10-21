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
}

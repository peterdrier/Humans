using AwesomeAssertions;
using Humans.Campaigns.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Campaigns.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the section shape for Campaigns
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// No EF-reference assertion here: the section assembly holds the repository and
/// legitimately references EF. Keeping the service off the DbSets is HUM0025's job.
/// </remarks>
public class CampaignsArchitectureTests
{
    [HumansFact]
    public void CampaignRepository_UsesDbContextFactory()
    {
        var ctor = typeof(CampaignRepository).GetConstructors().Single();
        ctor.GetParameters()
            .Should().ContainSingle(
                p => p.ParameterType == typeof(IDbContextFactory<CampaignsDbContext>),
                because: "the repository is registered as singleton and must create scoped contexts through its own peeled context's factory (nobodies-collective/Humans#858)");
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "repository should not capture scoped DbContext instances");
    }
}

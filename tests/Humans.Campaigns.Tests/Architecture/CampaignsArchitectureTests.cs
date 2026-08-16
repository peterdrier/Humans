using AwesomeAssertions;
using Humans.Campaigns.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Campaigns.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the section shape for Campaigns
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/CampaignsArchitectureTests.cs</c>. Its
/// <c>CampaignService_DoesNotReferenceEntityFrameworkCore</c> test is gone: it asserted that
/// <c>Humans.Application</c> carries no EF reference, and the section assembly holds the
/// repository and legitimately does — so over there the assertion is either false or vacuous.
/// Keeping the service off the DbSets is HUM0025's job now.
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

    /// <summary>
    /// Pins the set of types that may inject <see cref="ICampaignRepository"/>: the owning
    /// service and the repository implementation. A new consumer taking the repository directly
    /// would bypass the service layer and the single-writer rule for the <c>campaign*</c> tables.
    /// </summary>
    [HumansFact]
    public void ICampaignRepository_HasNoUnexpectedConsumers()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Humans.Campaigns.Services.CampaignService",
            "Humans.Campaigns.Data.CampaignRepository",
        };

        var consumers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ICampaignRepository))))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        consumers.Where(c => !allowed.Contains(c)).Should().BeEmpty(
            because: "every read/write to the campaign* tables must go through CampaignService");
    }
}

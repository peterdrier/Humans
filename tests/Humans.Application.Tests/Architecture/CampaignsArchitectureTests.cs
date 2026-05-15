using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Campaigns;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository/service migration shape for
/// Campaigns.
/// <list type="bullet">
/// <item><description>CampaignService lives in <c>Humans.Application.Services.Campaigns</c>.</description></item>
/// <item><description>CampaignService uses ICampaignRepository and does not take <c>DbContext</c>/<c>IDbContextFactory</c> directly.</description></item>
/// <item><description>CampaignRepository is sealed and registered as the owning
/// implementation of <see cref="ICampaignRepository"/>.</description></item>
/// <item><description>CampaignRepository is factory-based and does not capture scoped <c>DbContext</c>.</description></item>
/// </list>
/// </summary>
public class CampaignsArchitectureTests
{
    [HumansFact]
    public void CampaignService_TakesRepository()
    {
        var ctor = typeof(Humans.Application.Services.Campaigns.CampaignService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ICampaignRepository));
        paramTypes.Should().Contain(typeof(ITeamService));
        paramTypes.Should().Contain(typeof(IUserEmailService));
        paramTypes.Should().Contain(typeof(IUserService));
        paramTypes.Should().Contain(typeof(INotificationService));
        paramTypes.Should().Contain(typeof(ICommunicationPreferenceService));
        paramTypes.Should().Contain(typeof(IEmailService));
    }

    [HumansFact]
    public void CampaignService_DoesNotReferenceEntityFrameworkCore()
    {
        // Humans.Application.csproj does not reference EF Core; this remains a
        // hard boundary guard against accidental imports.
        var applicationAssembly = typeof(ICampaignService).Assembly;
        applicationAssembly.GetReferencedAssemblies()
            .Should().NotContain(
                a => a.Name == "Microsoft.EntityFrameworkCore",
                because: "Campaigns service should not depend on EF Core directly");
    }

    [HumansFact]
    public void CampaignRepository_IsSealed()
    {
        typeof(CampaignRepository).IsSealed.Should().BeTrue();
    }

    [HumansFact]
    public void CampaignRepository_ImplementsICampaignRepository()
    {
        typeof(CampaignRepository).IsAssignableTo(typeof(ICampaignRepository))
            .Should().BeTrue();
    }

    [HumansFact]
    public void CampaignRepository_LivesInHumansInfrastructureRepositoriesCampaignsNamespace()
    {
        typeof(CampaignRepository).Namespace
            .Should().Be("Humans.Infrastructure.Repositories.Campaigns",
                because: "repository implementation belongs in Campaigns infrastructure namespace");
    }

    [HumansFact]
    public void CampaignRepository_UsesDbContextFactory()
    {
        var ctor = typeof(CampaignRepository).GetConstructors().Single();
        ctor.GetParameters()
            .Should().ContainSingle(
                p => p.ParameterType == typeof(IDbContextFactory<HumansDbContext>),
                because: "Campaigns repository is registered as singleton and must create scoped contexts through the factory");
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "repository should not capture scoped DbContext instances");
    }
}

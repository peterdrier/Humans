using AwesomeAssertions;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Microsoft.EntityFrameworkCore;
using Xunit;
using TeamPageService = Humans.Application.Services.Teams.TeamPageService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 Application-layer shape for
/// <see cref="TeamPageService"/> — migrated as part of the Teams section
/// Part 1 split (<c>#540</c>, sub-task <c>#540b</c>).
///
/// <para>
/// TeamPageService owns no tables — it composes across <see cref="ITeamService"/>,
/// <see cref="IProfileService"/>, <see cref="ITeamResourceService"/>,
/// <see cref="IShiftManagementService"/>, and <see cref="IUserService"/>. No
/// repository is needed; the tests below guard that it never regains a
/// <c>DbContext</c> dependency.
/// </para>
/// </summary>
public class TeamPageArchitectureTests
{
    [HumansFact]
    public void TeamPageService_LivesInHumansApplicationServicesTeamsNamespace()
    {
        typeof(TeamPageService).Namespace
            .Should().Be("Humans.Application.Services.Teams",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [HumansFact]
    public void TeamPageService_ImplementsITeamPageService()
    {
        typeof(ITeamPageService).IsAssignableFrom(typeof(TeamPageService))
            .Should().BeTrue();
    }

    [HumansFact]
    public void TeamPageService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(TeamPageService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — cross-domain reads route through sibling service interfaces (design-rules §2c, §3)");
    }

    [HumansFact]
    public void TeamPageService_DependsOnlyOnSiblingServiceInterfaces()
    {
        var ctor = typeof(TeamPageService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITeamService));
        paramTypes.Should().Contain(typeof(IProfileService));
        paramTypes.Should().Contain(typeof(ITeamResourceService));
        paramTypes.Should().Contain(typeof(IShiftManagementService));
        paramTypes.Should().Contain(typeof(IUserService),
            because: "user display-name lookups route through IUserService instead of a direct AspNetUsers query (design-rules §2c)");
    }

    [HumansFact]
    public void TeamPageService_HasNoRepositoryDependencies()
    {
        var ctor = typeof(TeamPageService).GetConstructors().Single();
        var repoParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Repositories", StringComparison.Ordinal));

        repoParam.Should().BeNull(
            because: "TeamPageService owns no tables — it is a composer that stitches sibling services (design-rules §2c)");
    }

    [HumansFact]
    public void TeamPageService_IsSealed()
    {
        typeof(TeamPageService).IsSealed
            .Should().BeTrue(
                because: "application services are terminal; behavior changes belong on the interface");
    }
}

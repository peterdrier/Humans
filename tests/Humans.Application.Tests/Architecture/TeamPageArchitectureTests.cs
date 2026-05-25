using AwesomeAssertions;
using Humans.Application.Interfaces.Teams;
using TeamPageService = Humans.Application.Services.Teams.TeamPageService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 Application-layer shape for
/// <see cref="TeamPageService"/> — migrated as part of the Teams section
/// Part 1 split (<c>#540</c>, sub-task <c>#540b</c>).
///
/// <para>
/// TeamPageService owns no tables — it composes across <see cref="ITeamService"/>,
/// <see cref="ITeamResourceService"/>, <see cref="IShiftManagementService"/>,
/// and <see cref="IUserService"/>. No repository is needed; the tests below
/// guard that it never regains a <c>DbContext</c> dependency.
/// </para>
/// </summary>
public class TeamPageArchitectureTests
{
    [HumansFact]
    public void TeamPageService_ImplementsITeamPageService()
    {
        typeof(ITeamPageService).IsAssignableFrom(typeof(TeamPageService))
            .Should().BeTrue();
    }

    [HumansFact]
    public void TeamPageService_IsSealed()
    {
        typeof(TeamPageService).IsSealed
            .Should().BeTrue(
                because: "application services are terminal; behavior changes belong on the interface");
    }

    [HumansFact]
    public void TeamPageService_HasNoRepositoryDependencies()
    {
        // Orchestrator-no-repository guard. No universal enforcer covers this yet
        // (A2 deferred); HUM0017 only catches cross-section repository injection.
        var ctor = typeof(TeamPageService).GetConstructors().Single();
        var repoParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Repositories", StringComparison.Ordinal));

        repoParam.Should().BeNull(
            because: "TeamPageService owns no tables — it is a composer that stitches sibling services (design-rules §2c)");
    }
}

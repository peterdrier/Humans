using AwesomeAssertions;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Services;
using TeamPageService = Humans.Teams.Services.TeamPageService;

namespace Humans.Teams.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 Application-layer shape for
/// <see cref="TeamPageService"/> — migrated as part of the Teams section
/// Part 1 split (<c>#540</c>, sub-task <c>#540b</c>).
///
/// <para>
/// TeamPageService owns no tables — it composes across <see cref="ITeamService"/>,
/// <see cref="ITeamResourceService"/>, <see cref="IShiftManagementService"/>,
/// and <see cref="IUserService"/>. No repository is needed; the tests below
/// guard that it never takes one.
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
}

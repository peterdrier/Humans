using AwesomeAssertions;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Services;
using TeamPageService = Humans.Teams.Services.TeamPageService;

namespace Humans.Teams.Tests.Architecture;

/// <summary>
/// <see cref="TeamPageService"/> owns no tables — it composes across the management
/// service, <see cref="ITeamResourceService"/>, Shifts' and Users' read interfaces and
/// <c>IBurnSettingsService</c>. No repository is needed; the tests below guard that it
/// never takes one.
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

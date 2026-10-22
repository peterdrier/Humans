using AwesomeAssertions;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Services;
using TeamPageService = Humans.Teams.Services.TeamPageService;

namespace Humans.Teams.Tests.Architecture;

/// <summary>
/// <see cref="TeamPageService"/> composes other sections' read interfaces and owns no tables, so
/// it must never take a repository.
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

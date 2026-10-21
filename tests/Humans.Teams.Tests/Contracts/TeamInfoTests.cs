using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Teams.Tests.Contracts;

public sealed class TeamInfoTests
{
    [HumansTheory]
    [Xunit.InlineData(false, false, true)]
    [Xunit.InlineData(true, false, false)]
    [Xunit.InlineData(false, true, false)]
    public void CanBePublic_OnlyForNonSystemTopLevelTeams(bool isSystemTeam, bool hasParent, bool expected)
    {
        var team = new TeamInfo(Guid.NewGuid(), "Team", null, "team", true, isSystemTeam,
            SystemTeamType.None, false, false, false, false, Instant.FromUtc(2026, 1, 1, 0, 0), [],
            ParentTeamId: hasParent ? Guid.NewGuid() : null);

        team.CanBePublic.Should().Be(expected);
    }
}

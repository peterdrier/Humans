using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Services;
using NSubstitute;

namespace Humans.Teams.Tests.Services;

public sealed class TeamDriveAccessSourceTests
{
    [HumansFact]
    public async Task Shared_resource_unions_direct_and_active_child_members_at_their_highest_level()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var inactiveChildUser = Guid.NewGuid();
        var parent = Team(alice);
        var child = Team(bob) with { ParentTeamId = parent.Id };
        var other = Team(alice);
        var inactiveChild = Team(inactiveChildUser) with { ParentTeamId = parent.Id, IsActive = false };
        var source = Source([parent, child, other, inactiveChild],
            Resource(parent, "shared", DrivePermissionLevel.Viewer),
            Resource(other, "shared", DrivePermissionLevel.Manager));

        var claims = await source.GetExpectedAccessAsync(ct: Xunit.TestContext.Current.CancellationToken);

        claims.Should().ContainSingle();
        claims["shared"].Should().BeEquivalentTo(new Dictionary<Guid, DrivePermissionLevel>
        {
            [alice] = DrivePermissionLevel.Manager,
            [bob] = DrivePermissionLevel.Viewer
        });
    }

    [HumansFact]
    public async Task Inactive_and_unset_resources_stay_claimed_without_grants_and_groups_are_excluded()
    {
        var active = Team(Guid.NewGuid());
        var inactive = Team(Guid.NewGuid()) with { IsActive = false };
        var source = Source([active, inactive],
            Resource(active, "unset", DrivePermissionLevel.None),
            Resource(inactive, "retired", DrivePermissionLevel.Manager),
            Resource(Team(Guid.NewGuid()), "missing-team", DrivePermissionLevel.Manager),
            Resource(active, "group", DrivePermissionLevel.None) with { ResourceType = GoogleResourceType.Group });

        var claims = await source.GetExpectedAccessAsync(ct: Xunit.TestContext.Current.CancellationToken);

        claims.Keys.Should().BeEquivalentTo(["unset", "retired", "missing-team"]);
        claims.Values.Should().OnlyContain(grants => grants.Count == 0);
    }

    [HumansFact]
    public async Task Requested_file_is_claimed_without_returning_other_resources()
    {
        var userId = Guid.NewGuid();
        var team = Team(userId);
        var source = Source([team],
            Resource(team, "folder", DrivePermissionLevel.Viewer),
            Resource(team, "file", DrivePermissionLevel.Contributor) with { ResourceType = GoogleResourceType.DriveFile });

        var claims = await source.GetExpectedAccessAsync("file", Xunit.TestContext.Current.CancellationToken);

        claims.Should().ContainSingle();
        claims["file"].Should().ContainKey(userId).WhoseValue.Should().Be(DrivePermissionLevel.Contributor);
        (await source.GetExpectedAccessAsync("unknown", Xunit.TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    private static TeamDriveAccessSource Source(TeamInfo[] teamInfos, params GoogleResourceSnapshot[] resourceInfos)
    {
        var teams = Substitute.For<ITeamServiceRead>();
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(teamInfos.ToDictionary(t => t.Id));
        var resources = Substitute.For<ITeamResourceServiceRead>();
        resources.GetActiveResourceCountsByTeamAsync(Arg.Any<CancellationToken>())
            .Returns(resourceInfos.GroupBy(r => r.TeamId).ToDictionary(g => g.Key, g => g.Count()));
        resources.GetResourcesByTeamIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => resourceInfos.Where(r => call.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(r.TeamId))
                .GroupBy(r => r.TeamId).ToDictionary(g => g.Key,
                    g => (IReadOnlyList<GoogleResourceSnapshot>)g.ToList()));
        return new TeamDriveAccessSource(teams, resources);
    }

    private static TeamInfo Team(Guid userId) => new(Guid.NewGuid(), "Team", null, "team", true,
        false, SystemTeamType.None, false, false, false, false, default,
        [new TeamMemberInfo(Guid.NewGuid(), userId, "Human", null, null, TeamMemberRole.Member, default)]);

    private static GoogleResourceSnapshot Resource(TeamInfo team, string googleId, DrivePermissionLevel level) =>
        new(Guid.NewGuid(), team.Id, googleId, googleId, GoogleResourceType.DriveFolder, null,
            DrivePermissionLevel: level);
}

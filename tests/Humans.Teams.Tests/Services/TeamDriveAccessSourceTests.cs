using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Services;
using NodaTime;
using NSubstitute;

namespace Humans.Teams.Tests.Services;

public sealed class TeamDriveAccessSourceTests
{
    private readonly ITeamServiceRead _teams = Substitute.For<ITeamServiceRead>();
    private readonly ITeamResourceServiceRead _resources = Substitute.For<ITeamResourceServiceRead>();

    [HumansFact]
    public async Task SharedResource_UnionsMembersAndRollsActiveChildrenUpWithTheirMaximumLevel()
    {
        var sharedMember = Guid.NewGuid();
        var childMember = Guid.NewGuid();
        var retiredMember = Guid.NewGuid();
        var parent = Team("parent", sharedMember);
        var child = Team("child", childMember) with { ParentTeamId = parent.Id };
        var retired = Team("retired", retiredMember) with { ParentTeamId = parent.Id, IsActive = false };
        var peer = Team("peer", sharedMember, childMember);
        Seed([parent, child, retired, peer],
            Resource(parent.Id, "shared", DrivePermissionLevel.Viewer),
            Resource(peer.Id, "shared", DrivePermissionLevel.Contributor),
            Resource(child.Id, "child-only", DrivePermissionLevel.Contributor));

        var claims = await new TeamDriveAccessSource(_teams, _resources)
            .GetExpectedAccessAsync(ct: Xunit.TestContext.Current.CancellationToken);

        claims["shared"].Access.Should().HaveCount(2);
        claims["shared"].Access[sharedMember].Should().Be(DrivePermissionLevel.Contributor);
        claims["shared"].Access[childMember].Should().Be(DrivePermissionLevel.Contributor);
        claims["shared"].Access.Should().NotContainKey(retiredMember);
        claims["shared"].LinkedTeams!.Select(t => t.Slug).Should().BeEquivalentTo("parent", "peer");
        claims["shared"].MemberTeamLinks![childMember].Select(t => t.Slug).Should().BeEquivalentTo("child", "peer");
        claims["child-only"].Access.Keys.Should().Equal(childMember);
    }

    [HumansFact]
    public async Task RetiredAndNoAccessResources_KeepEmptyClaimsWhileGroupsAndUnlinkedRowsAreExcluded()
    {
        var retired = Team("retired", Guid.NewGuid()) with { IsActive = false };
        var active = Team("active", Guid.NewGuid());
        Seed([retired, active],
            Resource(retired.Id, "retired-file", DrivePermissionLevel.Contributor) with { ResourceType = GoogleResourceType.DriveFile },
            Resource(active.Id, "no-access", DrivePermissionLevel.None),
            Resource(active.Id, "group", DrivePermissionLevel.Contributor) with { ResourceType = GoogleResourceType.Group },
            Resource(active.Id, "unlinked", DrivePermissionLevel.Contributor) with { IsActive = false });

        var source = new TeamDriveAccessSource(_teams, _resources);
        var claims = await source.GetExpectedAccessAsync(ct: Xunit.TestContext.Current.CancellationToken);
        var filtered = await source.GetExpectedAccessAsync("retired-file", Xunit.TestContext.Current.CancellationToken);

        claims.Keys.Should().BeEquivalentTo("retired-file", "no-access");
        claims.Values.Should().OnlyContain(c => c.Access.Count == 0);
        filtered.Keys.Should().Equal("retired-file");
    }

    private void Seed(TeamInfo[] teams, params GoogleResourceSnapshot[] resources)
    {
        _teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(teams.ToDictionary(t => t.Id));
        _resources.GetResourcesByTeamIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(resources.GroupBy(r => r.TeamId).ToDictionary(g => g.Key, g => (IReadOnlyList<GoogleResourceSnapshot>)g.ToList()));
    }

    private static TeamInfo Team(string slug, params Guid[] users) => new(
        Guid.NewGuid(), slug, null, slug, true, false, SystemTeamType.None,
        false, false, false, false, Instant.MinValue,
        users.Select(id => new TeamMemberInfo(Guid.NewGuid(), id, slug, null, null,
            TeamMemberRole.Member, Instant.MinValue)).ToList());

    private static GoogleResourceSnapshot Resource(Guid teamId, string googleId, DrivePermissionLevel level) => new(
        Guid.NewGuid(), teamId, googleId, googleId, GoogleResourceType.DriveFolder, null,
        DrivePermissionLevel: level);
}

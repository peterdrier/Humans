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
    [HumansTheory]
    [Xunit.InlineData(DrivePermissionLevel.Viewer, DrivePermissionLevel.Viewer)]
    [Xunit.InlineData(DrivePermissionLevel.None, DrivePermissionLevel.Contributor)]
    public async Task SharedItem_ClaimsDirectAndChildMembersAtTheirHighestLinkedLevel(
        DrivePermissionLevel configuredLevel, DrivePermissionLevel childLevel)
    {
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var siblingId = Guid.NewGuid();
        var departedChildId = Guid.NewGuid();
        var direct = Guid.NewGuid();
        var child = Guid.NewGuid();
        var departed = Guid.NewGuid();
        var teams = Substitute.For<ITeamServiceRead>();
        var resources = Substitute.For<ITeamResourceServiceRead>();
        var parent = Team(parentId, "parent", direct);
        var childTeam = Team(childId, "child", child) with { ParentTeamId = parentId };
        var sibling = Team(siblingId, "sibling", direct);
        var inactiveChild = Team(departedChildId, "inactive", departed) with { ParentTeamId = parentId, IsActive = false };
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [parentId] = parent,
            [childId] = childTeam,
            [siblingId] = sibling,
            [departedChildId] = inactiveChild
        });
        resources.GetResourcesByTeamIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<GoogleResourceSnapshot>>
            {
                [parentId] = [Resource(parentId, "shared", configuredLevel)],
                [siblingId] = [Resource(siblingId, "shared", DrivePermissionLevel.Contributor)]
            });

        var claims = await new TeamDriveAccessSource(teams, resources)
            .GetExpectedAccessAsync("shared", Xunit.TestContext.Current.CancellationToken);

        var claim = claims.Should().ContainSingle().Subject.Value;
        claim.Access.Should().BeEquivalentTo(new Dictionary<Guid, DrivePermissionLevel>
        {
            [direct] = DrivePermissionLevel.Contributor,
            [child] = childLevel
        });
        claim.MemberTeams[direct].Select(t => t.Slug).Should().BeEquivalentTo(["parent", "sibling"]);
        claim.MemberTeams[child].Should().ContainSingle().Which.Slug.Should().Be("child");
        claim.PreserveHigherPermissions.Should().BeTrue();
        claim.AllowSharedAccess.Should().BeTrue();
    }

    [HumansFact]
    public async Task InactiveTeam_StillClaimsItsFilesWithAnEmptyRosterForReconciliation()
    {
        var teamId = Guid.NewGuid();
        var teams = Substitute.For<ITeamServiceRead>();
        var resources = Substitute.For<ITeamResourceServiceRead>();
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [teamId] = Team(teamId, "retired", Guid.NewGuid()) with { IsActive = false }
        });
        resources.GetResourcesByTeamIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<GoogleResourceSnapshot>>
            {
                [teamId] = [Resource(teamId, "file", DrivePermissionLevel.Contributor) with { ResourceType = GoogleResourceType.DriveFile }]
            });

        var claims = await new TeamDriveAccessSource(teams, resources)
            .GetExpectedAccessAsync(ct: Xunit.TestContext.Current.CancellationToken);

        claims["file"].Access.Should().BeEmpty();
        var resource = claims["file"].Resources.Should().ContainSingle().Which;
        claims["file"].RetiredResourceIds.Should().ContainSingle().Which.Should().Be(resource.Id);
    }

    private static TeamInfo Team(Guid id, string slug, Guid member) => new(
        id, slug, null, slug, true, false, SystemTeamType.None, false, false, false, false, Instant.MinValue,
        [new TeamMemberInfo(Guid.NewGuid(), member, "Human", "human@example.org", null, TeamMemberRole.Member, Instant.MinValue)]);

    private static GoogleResourceSnapshot Resource(Guid teamId, string googleId, DrivePermissionLevel level) => new(
        Guid.NewGuid(), teamId, googleId, googleId, GoogleResourceType.DriveFolder, null, DrivePermissionLevel: level);
}

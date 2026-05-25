using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Enums;
using NodaTime;
using NSubstitute;
using NotificationRecipientResolver = Humans.Application.Services.Notifications.NotificationRecipientResolver;

namespace Humans.Application.Tests.Notifications;

/// <summary>
/// Unit tests for <see cref="NotificationRecipientResolver"/>, the thin
/// adapter that <see cref="NotificationService.SendToTeamAsync"/> and
/// <see cref="NotificationService.SendToRoleAsync"/> route through to fetch
/// recipient sets without taking a direct dependency on
/// <see cref="ITeamService"/> / <see cref="IRoleAssignmentService"/> (which
/// would close a DI cycle).
/// </summary>
public class NotificationRecipientResolverTests
{
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IRoleAssignmentService _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
    private readonly NotificationRecipientResolver _resolver;

    public NotificationRecipientResolverTests()
    {
        _resolver = new NotificationRecipientResolver(_teamService, _roleAssignmentService);
    }

    [HumansFact]
    public async Task GetTeamNotificationInfoAsync_ReturnsNull_WhenTeamDoesNotExist()
    {
        var teamId = Guid.NewGuid();
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TeamInfo?>(null));

        var info = await _resolver.GetTeamNotificationInfoAsync(teamId);

        info.Should().BeNull();
    }

    [HumansFact]
    public async Task GetTeamNotificationInfoAsync_ProjectsTeamIntoNotificationInfo()
    {
        var teamId = Guid.NewGuid();
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();

        var teamInfo = new TeamInfo(
            teamId, "Build Team", Description: null, Slug: "build",
            IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
            RequiresApproval: false, IsPublicPage: false, IsHidden: false,
            IsPromotedToDirectory: false,
            CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
            Members: [
                new TeamMemberInfo(Guid.NewGuid(), u1, "U1", null, null, TeamMemberRole.Member, Instant.FromUtc(2026, 1, 1, 0, 0)),
                new TeamMemberInfo(Guid.NewGuid(), u2, "U2", null, null, TeamMemberRole.Member, Instant.FromUtc(2026, 1, 1, 0, 0)),
            ]);

        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TeamInfo?>(teamInfo));

        var info = await _resolver.GetTeamNotificationInfoAsync(teamId);

        info.Should().NotBeNull();
        info.Id.Should().Be(teamId);
        info.Name.Should().Be("Build Team");
        info.MemberUserIds.Should().BeEquivalentTo([u1, u2]);
    }

    [HumansFact]
    public async Task GetTeamNotificationInfoAsync_EmptyMembers_ReturnsEmptyList()
    {
        var teamId = Guid.NewGuid();
        var teamInfo = new TeamInfo(
            teamId, "Empty", Description: null, Slug: "empty",
            IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
            RequiresApproval: false, IsPublicPage: false, IsHidden: false,
            IsPromotedToDirectory: false,
            CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
            Members: []);

        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TeamInfo?>(teamInfo));

        var info = await _resolver.GetTeamNotificationInfoAsync(teamId);

        info.Should().NotBeNull();
        info.MemberUserIds.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetActiveUserIdsForRoleAsync_DelegatesToRoleAssignmentService()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();

        _roleAssignmentService.GetActiveUserIdsInRoleAsync("Board", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([u1, u2]));

        var ids = await _resolver.GetActiveUserIdsForRoleAsync("Board");

        ids.Should().BeEquivalentTo([u1, u2]);
        await _roleAssignmentService.Received(1)
            .GetActiveUserIdsInRoleAsync("Board", Arg.Any<CancellationToken>());
    }
}

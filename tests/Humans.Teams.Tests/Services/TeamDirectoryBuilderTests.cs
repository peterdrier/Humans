using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Base.Enums;
using Humans.Teams.Contracts;
using Humans.Teams.Services;
using NodaTime;
using NSubstitute;

namespace Humans.Teams.Tests.Services;

/// <summary>
/// The directory's visibility rules: hidden teams reach only Admin/TeamsAdmin/Board, sub-teams
/// appear only when promoted, anonymous visitors see public departments alone.
/// </summary>
public sealed class TeamDirectoryBuilderTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 5, 12, 0);
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();

    private static TeamInfo Team(
        string name,
        Guid? parentId = null,
        bool isHidden = false,
        bool isPublicPage = false,
        bool isPromoted = false,
        bool isActive = true,
        SystemTeamType systemType = SystemTeamType.None,
        params Guid[] memberIds) =>
        new(
            Guid.NewGuid(), name, null, name.ToLowerInvariant(),
            IsActive: isActive,
            IsSystemTeam: systemType != SystemTeamType.None,
            SystemTeamType: systemType,
            RequiresApproval: false,
            IsPublicPage: isPublicPage,
            IsHidden: isHidden,
            IsPromotedToDirectory: isPromoted,
            CreatedAt: Now,
            Members: memberIds
                .Select(id => new TeamMemberInfo(Guid.NewGuid(), id, "m", null, null, TeamMemberRole.Member, Now))
                .ToList(),
            ParentTeamId: parentId);

    private static IReadOnlyDictionary<Guid, TeamInfo> Graph(params TeamInfo[] teams) =>
        teams.ToDictionary(t => t.Id);

    [HumansFact]
    public async Task Anonymous_SeesPublicDepartmentsAndPromotedSubTeamsOnly()
    {
        var publicDept = Team("Build", isPublicPage: true);
        var privateDept = Team("Cantina");
        var hiddenPublic = Team("Secret", isHidden: true, isPublicPage: true);
        var inactivePublic = Team("Old", isPublicPage: true, isActive: false);
        var promotedChild = Team("Logo", parentId: publicDept.Id, isPromoted: true);
        var plainChild = Team("Signs", parentId: publicDept.Id);
        var system = Team("Volunteers", isPublicPage: true, systemType: SystemTeamType.Volunteers);

        var result = await TeamDirectoryBuilder.BuildAsync(
            Graph(publicDept, privateDept, hiddenPublic, inactivePublic, promotedChild, plainChild, system),
            _roles, userId: null, Xunit.TestContext.Current.CancellationToken);

        result.IsAuthenticated.Should().BeFalse();
        result.Departments.Select(t => t.Name).Should().BeEquivalentTo("Build", "Logo");
        result.MyTeams.Should().BeEmpty();
        result.SystemTeams.Should().BeEmpty();
        result.HiddenTeams.Should().BeEmpty();
        result.Departments.Single(t => string.Equals(t.Name, "Logo", StringComparison.Ordinal)).ParentTeamName.Should().Be("Build");
    }

    [HumansFact]
    public async Task Member_NeverSeesHiddenTeams_EvenTheirOwn()
    {
        var user = Guid.NewGuid();
        var dept = Team("Build", memberIds: user);
        var hidden = Team("Secret", isHidden: true, memberIds: user);
        var otherDept = Team("Cantina");
        var plainChild = Team("Signs", parentId: dept.Id);
        var system = Team("Volunteers", systemType: SystemTeamType.Volunteers);

        var result = await TeamDirectoryBuilder.BuildAsync(
            Graph(dept, hidden, otherDept, plainChild, system),
            _roles, user, Xunit.TestContext.Current.CancellationToken);

        result.CanCreateTeam.Should().BeFalse();
        result.MyTeams.Select(t => t.Name).Should().BeEquivalentTo("Build");
        result.Departments.Select(t => t.Name).Should().BeEquivalentTo("Cantina");
        result.SystemTeams.Select(t => t.Name).Should().BeEquivalentTo("Volunteers");
        result.HiddenTeams.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Admin_SeesHiddenTeamsAndMayCreate()
    {
        var admin = Guid.NewGuid();
        _roles.IsUserAdminAsync(admin, Arg.Any<CancellationToken>()).Returns(true);
        var dept = Team("Build");
        var hidden = Team("Secret", isHidden: true);

        var result = await TeamDirectoryBuilder.BuildAsync(
            Graph(dept, hidden), _roles, admin, Xunit.TestContext.Current.CancellationToken);

        result.CanCreateTeam.Should().BeTrue();
        result.Departments.Select(t => t.Name).Should().BeEquivalentTo("Build");
        result.HiddenTeams.Select(t => t.Name).Should().BeEquivalentTo("Secret");
    }
}

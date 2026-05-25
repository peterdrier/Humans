using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Teams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Pins the T-01 invariant: <see cref="CachingTeamService.GetTeamDetailAsync"/>
/// projects entirely from the cached <c>TeamInfo</c> snapshot.
/// </summary>
public sealed class CachingTeamServiceGetTeamDetailTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly ITeamService _innerTeamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly CachingTeamService _service;

    public CachingTeamServiceGetTeamDetailTests()
    {
        _innerTeamService = Substitute.For<ITeamService>();
        _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
        var userService = Substitute.For<IUserService>();

        userService
            .GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());

        var services = new ServiceCollection();
        services.AddSingleton(userService);
        services.AddSingleton<IUserServiceRead>(userService);
        services.AddSingleton(_roleAssignmentService);
        services.AddKeyedScoped<ITeamService>(
            CachingTeamService.InnerServiceKey,
            (_, _) => _innerTeamService);
        _serviceProvider = services.BuildServiceProvider();

        _service = new CachingTeamService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingTeamService>.Instance);
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_AnonymousVisible_DoesNotCallBypassedRepositoryReads()
    {
        var team = MakeTeam("Public", isPublicPage: true);
        SeedTeams(team);

        var result = await _service.GetTeamDetailAsync("public", userId: null);

        result.Should().NotBeNull();
        result.Team.Slug.Should().Be("public");
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_AnonymousHiddenTeam_ReturnsNullWithoutRepositoryReads()
    {
        var team = MakeTeam("Hidden", isPublicPage: true, isHidden: true);
        SeedTeams(team);

        var result = await _service.GetTeamDetailAsync("hidden", userId: null);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_AuthenticatedMember_ProjectsFromCacheWithoutBypassedReads()
    {
        var team = MakeTeam("Alpha", isPublicPage: true);
        var memberId = Guid.NewGuid();
        team.Members.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = memberId,
            Role = TeamMemberRole.Member,
            JoinedAt = Instant.FromUtc(2026, 1, 1, 0, 0)
        });
        SeedTeams(team);

        _innerTeamService
            .GetUserPendingRequestAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TeamJoinRequestSnapshot?)null);
        _roleAssignmentService.IsUserBoardMemberAsync(memberId, Arg.Any<CancellationToken>()).Returns(false);
        _roleAssignmentService.IsUserAdminAsync(memberId, Arg.Any<CancellationToken>()).Returns(false);
        _roleAssignmentService.IsUserTeamsAdminAsync(memberId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.GetTeamDetailAsync("alpha", memberId);

        result.Should().NotBeNull();
        result.IsAuthenticated.Should().BeTrue();
        result.IsCurrentUserMember.Should().BeTrue();
        result.CanCurrentUserLeave.Should().BeTrue();
        result.CanCurrentUserJoin.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_ResolvesViaCustomSlug()
    {
        var team = MakeTeam("Original", isPublicPage: true);
        team.CustomSlug = "branded";
        SeedTeams(team);

        var result = await _service.GetTeamDetailAsync("Branded", userId: null);

        result.Should().NotBeNull();
        result.Team.Slug.Should().Be("original");
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_AuthenticatedManager_WalksChildTeamIdsFromCache()
    {
        var parent = MakeTeam("Department", isPublicPage: true);
        var child = MakeTeam("Subteam");
        child.ParentTeamId = parent.Id;
        SeedTeams(parent, child);

        var viewerId = Guid.NewGuid();
        _innerTeamService
            .GetUserPendingRequestAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TeamJoinRequestSnapshot?)null);
        _roleAssignmentService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>()).Returns(false);
        _roleAssignmentService.IsUserAdminAsync(viewerId, Arg.Any<CancellationToken>()).Returns(false);
        _roleAssignmentService.IsUserTeamsAdminAsync(viewerId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.GetTeamDetailAsync("department", viewerId);

        result.Should().NotBeNull();
        result.ChildTeams.Should().ContainSingle(c => c.Id == child.Id);
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_AnonymousChildVisibility_FiltersHiddenAndPrivateChildren()
    {
        var parent = MakeTeam("Dept", isPublicPage: true);
        var publicChild = MakeTeam("PublicChild", isPublicPage: true);
        publicChild.ParentTeamId = parent.Id;
        var hiddenChild = MakeTeam("HiddenChild", isPublicPage: true, isHidden: true);
        hiddenChild.ParentTeamId = parent.Id;
        var privateChild = MakeTeam("PrivateChild", isPublicPage: false);
        privateChild.ParentTeamId = parent.Id;
        SeedTeams(parent, publicChild, hiddenChild, privateChild);

        var result = await _service.GetTeamDetailAsync("dept", userId: null);

        result.Should().NotBeNull();
        result.ChildTeams.Select(c => c.Id).Should().BeEquivalentTo([publicChild.Id]);
    }

    private void SeedTeams(params Team[] teams)
    {
        var childIdsByParent = teams
            .Where(t => t.ParentTeamId.HasValue)
            .GroupBy(t => t.ParentTeamId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(t => t.Id).ToList());
        var infos = teams.ToDictionary(
            t => t.Id,
            t => new TeamInfo(
                t.Id,
                t.Name,
                t.Description,
                t.Slug,
                t.IsActive,
                t.IsSystemTeam,
                t.SystemTeamType,
                t.RequiresApproval,
                t.IsPublicPage,
                t.IsHidden,
                t.IsPromotedToDirectory,
                t.CreatedAt,
                t.Members
                    .Where(m => m.LeftAt is null)
                    .Select(m => new TeamMemberInfo(
                        m.Id,
                        m.UserId,
                        string.Empty,
                        null,
                        null,
                        m.Role,
                        m.JoinedAt))
                    .ToList(),
                ParentTeamId: t.ParentTeamId,
                GoogleGroupPrefix: t.GoogleGroupPrefix,
                HasBudget: t.HasBudget,
                IsSensitive: t.IsSensitive,
                UpdatedAt: t.UpdatedAt,
                CustomSlug: t.CustomSlug,
                ChildTeamIds: childIdsByParent.TryGetValue(t.Id, out var childIds) ? childIds : null,
                ShowCoordinatorsOnPublicPage: t.ShowCoordinatorsOnPublicPage,
                PageContent: t.PageContent,
                CallsToAction: t.CallsToAction,
                PageContentUpdatedAt: t.PageContentUpdatedAt,
                PageContentUpdatedByUserId: t.PageContentUpdatedByUserId));
        _innerTeamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(infos);
    }

    private static Team MakeTeam(string name, bool isPublicPage = false, bool isHidden = false)
    {
        return new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = name.ToLowerInvariant(),
            IsActive = true,
            IsPublicPage = isPublicPage,
            IsHidden = isHidden,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0)
        };
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }
}

using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Teams;

/// <summary>
/// Application-layer composer for the public/member team page. Owns no tables
/// — stitches data from <see cref="ITeamService"/>, <see cref="IProfileService"/>,
/// <see cref="ITeamResourceService"/>, <see cref="IShiftManagementService"/>,
/// and <see cref="IUserService"/>.
/// </summary>
/// <remarks>
/// Part of §15 Part 1 Teams migration (<c>#540</c>). TeamPageService moved
/// out of <c>Humans.Infrastructure</c> first because it is a pure composer
/// with no owned tables; the larger <c>TeamService</c>, <c>TeamResourceService</c>,
/// and <c>StubTeamResourceService</c> migrate in separate sub-tasks.
/// </remarks>
public sealed class TeamPageService : ITeamPageService
{
    private readonly ITeamService _teamService;
    private readonly IProfileService _profileService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly IShiftManagementService _shiftManagementService;
    private readonly IUserService _userService;

    public TeamPageService(
        ITeamService teamService,
        IProfileService profileService,
        ITeamResourceService teamResourceService,
        IShiftManagementService shiftManagementService,
        IUserService userService)
    {
        _teamService = teamService;
        _profileService = profileService;
        _teamResourceService = teamResourceService;
        _shiftManagementService = shiftManagementService;
        _userService = userService;
    }

    public async Task<TeamPageDetailResult?> GetTeamPageDetailAsync(
        string slug,
        Guid? userId,
        bool canManageShiftsByRole,
        CancellationToken cancellationToken = default)
    {
        var detail = await _teamService.GetTeamDetailAsync(slug, userId, cancellationToken);
        if (detail is null)
        {
            return null;
        }

        // For anonymous users, filter members based on coordinator visibility setting
        var visibleMembers = detail.IsAuthenticated
            ? detail.Members
            : detail.Team.ShowCoordinatorsOnPublicPage
                ? detail.Members.Where(m => m.Role == TeamMemberRole.Coordinator).ToList()
                : [];

        var customPictures = await GetCustomPicturesByUserIdAsync(
            visibleMembers,
            cancellationToken);
        var members = visibleMembers
            .Select(member => new TeamPageMemberSummary(
                member.UserId,
                member.DisplayName,
                detail.IsAuthenticated ? member.Email : null,
                member.ProfilePictureUrl,
                member.Role,
                detail.IsAuthenticated ? member.JoinedAt : null,
                customPictures.GetValueOrDefault(member.UserId)))
            .ToList();

        var pageContentUpdatedByDisplayName = await GetPageContentUpdatedByDisplayNameAsync(
            detail.Team.PageContentUpdatedByUserId,
            cancellationToken);

        var resources = detail.IsAuthenticated
            ? (await _teamResourceService.GetTeamResourcesAsync(detail.Team.Id, cancellationToken))
                .Select(resource => new TeamPageResourceSummary(
                    resource.Name,
                    resource.Url ?? string.Empty,
                    resource.ResourceType))
                .ToList()
            : [];

        var shiftsSummary = await GetShiftsSummaryAsync(
            detail.Team,
            detail.ChildTeams,
            userId,
            detail.IsAuthenticated,
            canManageShiftsByRole,
            cancellationToken);

        return new TeamPageDetailResult(
            detail.Team,
            members,
            detail.ChildTeams,
            detail.RoleDefinitions,
            resources,
            detail.IsAuthenticated,
            detail.IsCurrentUserMember,
            detail.IsCurrentUserCoordinator,
            detail.CanCurrentUserJoin,
            detail.CanCurrentUserLeave,
            detail.CanCurrentUserManage,
            detail.CanCurrentUserEditTeam,
            detail.CurrentUserPendingRequestId,
            detail.PendingRequestCount,
            pageContentUpdatedByDisplayName,
            shiftsSummary);
    }

    private async Task<Dictionary<Guid, TeamPageCustomPicture>> GetCustomPicturesByUserIdAsync(
        IReadOnlyList<TeamDetailMemberSummary> members,
        CancellationToken cancellationToken)
    {
        if (members.Count == 0)
        {
            return [];
        }

        var customPictures = await _profileService.GetCustomPictureInfoByUserIdsAsync(
            members.Select(member => member.UserId),
            cancellationToken);

        return customPictures.ToDictionary(
            picture => picture.UserId,
            picture => new TeamPageCustomPicture(picture.ProfileId, picture.UpdatedAtTicks));
    }

    private async Task<string?> GetPageContentUpdatedByDisplayNameAsync(
        Guid? userId,
        CancellationToken cancellationToken)
    {
        if (!userId.HasValue)
        {
            return null;
        }

        var user = await _userService.GetByIdAsync(userId.Value, cancellationToken);
        return user?.DisplayName;
    }

    private async Task<TeamPageShiftsSummary?> GetShiftsSummaryAsync(
        Team team,
        IReadOnlyList<Team> childTeams,
        Guid? userId,
        bool isAuthenticated,
        bool canManageShiftsByRole,
        CancellationToken cancellationToken)
    {
        if (!isAuthenticated ||
            !userId.HasValue ||
            team.SystemTeamType != SystemTeamType.None)
        {
            return null;
        }

        var canManageShifts = canManageShiftsByRole ||
            await _shiftManagementService.IsDeptCoordinatorAsync(userId.Value, team.Id);

        var activeEvent = await _shiftManagementService.GetActiveAsync();
        if (activeEvent is null)
        {
            return new TeamPageShiftsSummary(0, 0, 0, 0, canManageShifts);
        }

        // For parent teams, aggregate shifts from the parent team plus all active child teams.
        // detail.ChildTeams (authenticated path) is team.ChildTeams filtered to IsActive.
        var activeChildTeamIds = childTeams.Select(c => c.Id).ToList();

        if (activeChildTeamIds.Count > 0)
        {
            var allTeamIds = new List<Guid>(activeChildTeamIds.Count + 1) { team.Id };
            allTeamIds.AddRange(activeChildTeamIds);

            var aggregatedData = await _shiftManagementService.GetShiftsSummaryForTeamsAsync(activeEvent.Id, allTeamIds);
            if (aggregatedData is null)
            {
                return new TeamPageShiftsSummary(0, 0, 0, 0, canManageShifts);
            }

            // Count only child teams that actually have shifts in the active event
            var childTeamIdsWithShifts = await _shiftManagementService.GetTeamIdsWithShiftsInEventAsync(
                activeEvent.Id, activeChildTeamIds, cancellationToken);

            return new TeamPageShiftsSummary(
                aggregatedData.TotalSlots,
                aggregatedData.ConfirmedCount,
                aggregatedData.PendingCount,
                aggregatedData.UniqueVolunteerCount,
                canManageShifts,
                childTeamIdsWithShifts.Count);
        }

        // Child team or standalone team: show only own shifts
        var summaryData = await _shiftManagementService.GetShiftsSummaryAsync(activeEvent.Id, team.Id);
        if (summaryData is null)
        {
            return new TeamPageShiftsSummary(0, 0, 0, 0, canManageShifts);
        }

        return new TeamPageShiftsSummary(
            summaryData.TotalSlots,
            summaryData.ConfirmedCount,
            summaryData.PendingCount,
            summaryData.UniqueVolunteerCount,
            canManageShifts);
    }
}

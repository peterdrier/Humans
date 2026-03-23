using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Services;

public class TeamPageService : ITeamPageService
{
    private readonly ITeamService _teamService;
    private readonly IProfileService _profileService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly IShiftManagementService _shiftManagementService;
    private readonly HumansDbContext _dbContext;

    public TeamPageService(
        ITeamService teamService,
        IProfileService profileService,
        ITeamResourceService teamResourceService,
        IShiftManagementService shiftManagementService,
        HumansDbContext dbContext)
    {
        _teamService = teamService;
        _profileService = profileService;
        _teamResourceService = teamResourceService;
        _shiftManagementService = shiftManagementService;
        _dbContext = dbContext;
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
            visibleMembers as IReadOnlyList<TeamDetailMemberSummary> ?? visibleMembers.ToList(),
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

        return await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId.Value)
            .Select(user => user.DisplayName)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<TeamPageShiftsSummary?> GetShiftsSummaryAsync(
        Team team,
        Guid? userId,
        bool isAuthenticated,
        bool canManageShiftsByRole,
        CancellationToken cancellationToken)
    {
        if (!isAuthenticated ||
            !userId.HasValue ||
            team.ParentTeamId.HasValue ||
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

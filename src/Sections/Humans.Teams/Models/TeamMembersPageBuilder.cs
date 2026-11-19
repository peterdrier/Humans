using Humans.Base.Extensions;
using Humans.Google.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Services;
using Humans.Users.Contracts;

namespace Humans.Teams.Models;

internal sealed class TeamMembersPageBuilder(
    ITeamManagementService teamService,
    ITeamResourceService teamResourceService,
    IUserService userService)
{
    public async Task<TeamMembersViewModel> BuildAsync(
        TeamInfo team, Guid actorUserId, bool canManage, int page, CancellationToken ct = default)
    {
        var pageSize = 20;
        var allMembers = team.Members
            .OrderBy(m => m.Role)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var totalCount = allMembers.Count;

        var pagedMembers = allMembers
            .Skip((int)Math.Min(((long)page - 1) * pageSize, totalCount))
            .Take(pageSize)
            .ToList();

        var pagedMemberInfos = await userService.GetUserInfosAsync(pagedMembers.Select(m => m.UserId).ToList(), ct);

        var members = pagedMembers
            .Select(m =>
            {
                var nobodiesEmail = pagedMemberInfos.TryGetValue(m.UserId, out var info)
                    ? info.UserEmails.FirstOrDefault(e => e.IsVerified
                        && e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase))?.Email
                    : null;

                return new TeamMemberViewModel
                {
                    UserId = m.UserId,
                    Email = m.Email ?? "",
                    Role = m.Role,
                    JoinedAt = m.JoinedAt.ToDateTimeUtc(),
                    IsCoordinator = m.Role == TeamMemberRole.Coordinator,
                    NobodiesTeamEmail = nobodiesEmail,
                    LiveUserId = info?.Id ?? m.UserId
                };
            }).ToList();

        var pendingRequests = await teamService.GetPendingRequestsForTeamAsync(team.Id, ct);
        var pendingRequestViewModels = pendingRequests
            .Select(r => new TeamJoinRequestViewModel
            {
                Id = r.Id,
                UserId = r.UserId,
                Message = r.Message,
                RequestedAt = r.RequestedAt.ToDateTimeUtc()
            }).ToList();

        var allTeamResources = await teamResourceService.GetTeamResourcesAsync(team.Id, ct);
        var teamResources = allTeamResources.Where(r => r.IsActive).OrderBy(r => r.ResourceType).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();

        var parentDepartmentResources = new List<GoogleResourceSnapshot>();
        string? parentDepartmentName = null;
        if (team.ParentTeamId is { } parentTeamId &&
            await teamService.GetTeamAsync(parentTeamId, ct) is { } parentTeam)
        {
            parentDepartmentName = parentTeam.Name;
            var allParentResources = await teamResourceService.GetTeamResourcesAsync(parentTeam.Id, ct);
            parentDepartmentResources = allParentResources.Where(r => r.IsActive).OrderBy(r => r.ResourceType).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        }

        var viewModel = new TeamMembersViewModel
        {
            TeamId = team.Id,
            TeamName = team.Name,
            TeamSlug = team.Slug,
            IsSystemTeam = team.IsSystemTeam,
            CanManage = canManage,
            CanManageRoles = canManage && !team.IsSystemTeam,
            CanProvisionEmails = canManage,
            Members = members,
            AllMemberUserIds = allMembers.Select(m => m.UserId).ToList(),
            PendingRequests = pendingRequestViewModels,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize,
            TeamResources = teamResources.Select(MapResourceAccess).ToList(),
            ParentDepartmentResources = parentDepartmentResources.Select(MapResourceAccess).ToList(),
            ParentDepartmentName = parentDepartmentName,
            IsSensitive = team.IsSensitive,
            ActorUserId = actorUserId
        };

        return viewModel;
    }

    private static ResourceAccessViewModel MapResourceAccess(GoogleResourceSnapshot r) => new()
    {
        Name = r.Name,
        ResourceType = r.ResourceType switch
        {
            GoogleResourceType.DriveFolder => "Drive Folder",
            GoogleResourceType.SharedDrive => "Shared Drive",
            GoogleResourceType.DriveFile => "Drive File",
            GoogleResourceType.Group => "Google Group",
            _ => r.ResourceType.ToString()
        },
        PermissionLevel = r.ResourceType == GoogleResourceType.Group
            ? null
            : r.DrivePermissionLevel != DrivePermissionLevel.None
                ? r.DrivePermissionLevel.ToString()
                : null,
        Url = r.Url,
        IconClass = r.ResourceType switch
        {
            GoogleResourceType.DriveFolder => "fa-solid fa-folder",
            GoogleResourceType.SharedDrive => "fa-solid fa-hard-drive",
            GoogleResourceType.DriveFile => "fa-solid fa-file",
            GoogleResourceType.Group => "fa-solid fa-users",
            _ => "fa-solid fa-link"
        }
    };

}

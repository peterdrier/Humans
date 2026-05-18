#pragma warning disable CS0618 // TeamMember.User / TeamJoinRequest.User — populated in-memory by TeamService (§6b).
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Web.Authorization;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Teams/{slug}")]
public class TeamAdminController : HumansTeamControllerBase
{
    private readonly ITeamService _teamService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IProfileService _profileService;
    private readonly IUserService _userService;
    private readonly IEmailProvisioningService _emailProvisioningService;
    private readonly INotificationService _notificationService;
    private readonly ISystemTeamSync _systemTeamSyncJob;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TeamAdminController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public TeamAdminController(
        ITeamService teamService,
        ITeamResourceService teamResourceService,
        IGoogleSyncService googleSyncService,
        IProfileService profileService,
        IUserService userService,
        IEmailProvisioningService emailProvisioningService,
        INotificationService notificationService,
        IAuthorizationService authorizationService,
        ISystemTeamSync systemTeamSyncJob,
        IMemoryCache cache,
        ILogger<TeamAdminController> logger,
        IStringLocalizer<SharedResource> localizer)
        : base(userService, teamService, authorizationService)
    {
        _teamService = teamService;
        _teamResourceService = teamResourceService;
        _googleSyncService = googleSyncService;
        _profileService = profileService;
        _userService = userService;
        _emailProvisioningService = emailProvisioningService;
        _notificationService = notificationService;
        _systemTeamSyncJob = systemTeamSyncJob;
        _cache = cache;
        _logger = logger;
        _localizer = localizer;
    }

    [HttpPost("Requests/{requestId}/Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveRequest(string slug, Guid requestId, ApproveRejectRequestModel model)
    {
        var (teamError, user, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        try
        {
            await _teamService.ApproveJoinRequestAsync(requestId, user.Id, model.Notes);
            SetSuccess(_localizer["TeamAdmin_RequestApproved"].Value);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to approve join request {RequestId} for team {TeamId} by user {UserId}", requestId, team.Id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Members), new { slug });
    }

    [HttpPost("Requests/{requestId}/Reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectRequest(string slug, Guid requestId, ApproveRejectRequestModel model)
    {
        var (teamError, user, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        if (string.IsNullOrWhiteSpace(model.Notes))
        {
            SetError(_localizer["TeamAdmin_ProvideRejectionReason"].Value);
            return RedirectToAction(nameof(Members), new { slug });
        }

        try
        {
            await _teamService.RejectJoinRequestAsync(requestId, user.Id, model.Notes);
            SetSuccess(_localizer["TeamAdmin_RequestRejected"].Value);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to reject join request {RequestId} for team {TeamId} by user {UserId}", requestId, team.Id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Members), new { slug });
    }

    [HttpGet("Members")]
    public async Task<IActionResult> Members(string slug, int page = 1)
    {
        var pageSize = 20;
        var (teamError, user, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        var allMembers = team.Members
            .OrderBy(m => m.Role)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var totalCount = allMembers.Count;

        var pagedMembers = allMembers
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var memberUserIds = pagedMembers.Select(m => m.UserId).ToList();
        var memberInfos = await _userService.GetUserInfosAsync(memberUserIds);

        var members = pagedMembers
            .Select(m => new TeamMemberViewModel
            {
                UserId = m.UserId,
                DisplayName = m.DisplayName,
                Email = m.Email ?? "",
                ProfilePictureUrl = memberInfos.GetValueOrDefault(m.UserId)?.ProfilePictureUrl,
                Role = m.Role,
                JoinedAt = m.JoinedAt.ToDateTimeUtc(),
                IsCoordinator = m.Role == TeamMemberRole.Coordinator
            }).ToList();

        var pendingRequests = await _teamService.GetPendingRequestsForTeamAsync(team.Id);
        var pendingRequestViewModels = pendingRequests
            .Select(r => new TeamJoinRequestViewModel
            {
                Id = r.Id,
                TeamId = r.TeamId,
                TeamName = team.Name,
                UserId = r.UserId,
                UserDisplayName = r.UserDisplayName ?? "",
                UserEmail = r.UserEmail ?? "",
                UserProfilePictureUrl = r.UserProfilePictureUrl,
                Status = r.Status,
                Message = r.Message,
                RequestedAt = r.RequestedAt.ToDateTimeUtc()
            }).ToList();

        var allTeamResources = await _teamResourceService.GetTeamResourcesAsync(team.Id);
        var teamResources = allTeamResources.Where(r => r.IsActive).OrderBy(r => r.ResourceType).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();

        var parentDepartmentResources = new List<GoogleResourceSnapshot>();
        string? parentDepartmentName = null;
        string? parentDepartmentSlug = null;
        if (team.ParentTeamId is { } parentTeamId &&
            await _teamService.GetTeamAsync(parentTeamId) is { } parentTeam)
        {
            parentDepartmentName = parentTeam.Name;
            parentDepartmentSlug = parentTeam.CustomSlug ?? parentTeam.Slug;
            var allParentResources = await _teamResourceService.GetTeamResourcesAsync(parentTeam.Id);
            parentDepartmentResources = allParentResources.Where(r => r.IsActive).OrderBy(r => r.ResourceType).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        }

        static ResourceAccessViewModel MapResource(GoogleResourceSnapshot r) => new()
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

        var viewModel = new TeamMembersViewModel
        {
            TeamId = team.Id,
            TeamName = team.Name,
            TeamSlug = team.Slug,
            IsSystemTeam = team.IsSystemTeam,
            CanManageRoles = !team.IsSystemTeam,
            CanProvisionEmails = true,
            Members = members,
            AllMemberUserIds = allMembers.Select(m => m.UserId).ToList(),
            PendingRequests = pendingRequestViewModels,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize,
            TeamResources = teamResources.Select(MapResource).ToList(),
            ParentDepartmentResources = parentDepartmentResources.Select(MapResource).ToList(),
            ParentDepartmentName = parentDepartmentName,
            ParentDepartmentSlug = parentDepartmentSlug,
            IsSensitive = team.IsSensitive,
            ActorDisplayName = user.BurnerName
        };

        return View(viewModel);
    }

    [HttpPost("Members/{userId}/Remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(string slug, Guid userId)
    {
        var (teamError, user, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        try
        {
            var wasCoordinator = await _teamService.RemoveMemberAsync(team.Id, userId, user.Id);
            if (wasCoordinator)
            {
                await _systemTeamSyncJob.SyncMembershipForUserAsync(userId, SystemTeamType.Coordinators);
            }
            SetSuccess(_localizer["TeamAdmin_MemberRemoved"].Value);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to remove member {MemberUserId} from team {TeamId} by user {UserId}", userId, team.Id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Members), new { slug });
    }

    [HttpPost("Members/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMember(string slug, AddMemberModel model)
    {
        var (teamError, user, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        // Reject empty userId at controller — google_sync_outbox FK rejects Guid.Empty.
        if (model.UserId == Guid.Empty)
        {
            SetError("Select a user to add.");
            return RedirectToAction(nameof(Members), new { slug });
        }

        try
        {
            await _teamService.AddMemberToTeamAsync(team.Id, model.UserId, user.Id);
            SetSuccess(_localizer["TeamAdmin_MemberAdded"].Value);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to add member {MemberUserId} to team {TeamId} by user {UserId}", model.UserId, team.Id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Members), new { slug });
    }

    [HttpPost("Members/{userId}/ProvisionEmail")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProvisionEmail(string slug, Guid userId, string emailPrefix)
    {
        var (teamError, user, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        if (string.IsNullOrWhiteSpace(emailPrefix))
        {
            SetError("Email prefix is required.");
            return RedirectToAction(nameof(Members), new { slug });
        }

        var teamInfo = await _teamService.GetTeamAsync(team.Id);
        if (teamInfo is null || teamInfo.Members.All(m => m.UserId != userId))
        {
            SetError("That human is not a member of this team.");
            return RedirectToAction(nameof(Members), new { slug });
        }

        var result = await _emailProvisioningService.ProvisionNobodiesEmailAsync(
            userId, emailPrefix, user.Id);

        if (!result.Success)
        {
            SetError(result.ErrorMessage ?? "Provisioning failed.");
        }
        else
        {
            _cache.InvalidateNobodiesTeamEmails();

            if (result.RecoveryEmail is not null)
            {
                SetSuccess($"Account {result.FullEmail} provisioned and linked. Credentials sent to {result.RecoveryEmail}.");
            }
            else
            {
                SetSuccess($"Account {result.FullEmail} provisioned and linked. No recovery email found — credentials not sent.");
            }
        }

        return RedirectToAction(nameof(Members), new { slug });
    }

    [HttpGet("Members/Search")]
    public async Task<IActionResult> SearchUsers(string slug, string q)
    {
        var (teamError, _, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        if (!q.HasSearchTerm())
        {
            return Json(Array.Empty<HumanLookupSearchResult>());
        }

        // Name-only — team admins are not global admins, so no contact-data search.
        var results = await _userService.SearchUsersAsync(
            q, PersonSearchFields.Name, limit: 50);

        var teamInfo = await _teamService.GetTeamAsync(team.Id);
        var existingMemberIds = teamInfo?.Members.Select(m => m.UserId).ToHashSet() ?? [];

        // Display sort at controller (memory/architecture/display-sort-in-controllers.md).
        var filtered = results
            .Where(r => !existingMemberIds.Contains(r.UserId))
            .OrderBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(r => new HumanLookupSearchResult(r.UserId, r.BurnerName))
            .ToList();

        return Json(filtered);
    }

    [HttpGet("Resources")]
    public async Task<IActionResult> Resources(string slug)
    {
        var (currentUserNotFound, user) = await RequireCurrentUserAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team is null)
        {
            return NotFound();
        }

        if (!await CanManageResourcesAsync(team, user.Id))
        {
            return Forbid();
        }

        var resources = await _teamResourceService.GetTeamResourcesAsync(team.Id);
        var serviceAccountEmail = await _teamResourceService.GetServiceAccountEmailAsync();

        var viewModel = new TeamResourcesViewModel
        {
            TeamId = team.Id,
            TeamName = team.Name,
            TeamSlug = team.Slug,
            ServiceAccountEmail = serviceAccountEmail,
            Resources = resources.Select(r => new GoogleResourceViewModel
            {
                Id = r.Id,
                ResourceType = r.ResourceType switch
                {
                    GoogleResourceType.DriveFolder => "Drive Folder",
                    GoogleResourceType.SharedDrive => "Shared Drive",
                    GoogleResourceType.Group => "Google Group",
                    GoogleResourceType.DriveFile => "Drive File",
                    _ => r.ResourceType.ToString()
                },
                Name = r.Name,
                Url = r.Url,
                GoogleId = r.GoogleId,
                ProvisionedAt = r.ProvisionedAt.ToDateTimeUtc(),
                LastSyncedAt = r.LastSyncedAt?.ToDateTimeUtc(),
                IsActive = r.IsActive,
                ErrorMessage = r.ErrorMessage,
                DrivePermissionLevel = r.DrivePermissionLevel,
                IsDriveResource = r.ResourceType is GoogleResourceType.DriveFolder or GoogleResourceType.DriveFile or GoogleResourceType.SharedDrive,
                RestrictInheritedAccess = r.RestrictInheritedAccess,
                IsDriveFolder = r.ResourceType is GoogleResourceType.DriveFolder
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpPost("Resources/LinkDrive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkDriveResource(string slug, LinkDriveResourceModel model)
    {
        var (currentUserNotFound, user) = await RequireCurrentUserAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team is null)
        {
            return NotFound();
        }

        if (!await CanManageResourcesAsync(team, user.Id))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            SetError(_localizer["TeamAdmin_InvalidDriveUrl"].Value);
            return RedirectToAction(nameof(Resources), new { slug });
        }

        var result = await _teamResourceService.LinkDriveResourceAsync(team.Id, model.ResourceUrl, model.PermissionLevel);
        SetDriveResourceLinkResult(result);

        return RedirectToAction(nameof(Resources), new { slug });
    }

    [HttpPost("Resources/LinkGroup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkGroup(string slug, LinkGroupModel model)
    {
        var (currentUserNotFound, user) = await RequireCurrentUserAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team is null)
        {
            return NotFound();
        }

        if (!await CanManageResourcesAsync(team, user.Id))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            SetError(_localizer["TeamAdmin_InvalidGroupEmail"].Value);
            return RedirectToAction(nameof(Resources), new { slug });
        }

        var result = await _teamResourceService.LinkGroupAsync(team.Id, model.GroupEmail);
        SetGroupResourceLinkResult(result);

        return RedirectToAction(nameof(Resources), new { slug });
    }

    [HttpPost("Resources/{resourceId}/PermissionLevel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePermissionLevel(string slug, Guid resourceId, DrivePermissionLevel level)
    {
        var (currentUserNotFound, user) = await RequireCurrentUserAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team is null)
        {
            return NotFound();
        }

        if (!await CanManageResourcesAsync(team, user.Id))
        {
            return Forbid();
        }

        if (level == DrivePermissionLevel.None)
        {
            SetError("Invalid permission level.");
            return RedirectToAction(nameof(Resources), new { slug });
        }

        await _teamResourceService.UpdatePermissionLevelAsync(resourceId, level);
        SetSuccess($"Permission level updated to {level}.");

        return RedirectToAction(nameof(Resources), new { slug });
    }

    [HttpPost("Resources/{resourceId}/RestrictInheritedAccess")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleRestrictInheritedAccess(string slug, Guid resourceId, bool restrict)
    {
        var (currentUserNotFound, user) = await RequireCurrentUserAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team is null)
        {
            return NotFound();
        }

        if (!await CanManageResourcesAsync(team, user.Id))
        {
            return Forbid();
        }

        var result = await _teamResourceService.SetRestrictInheritedAccessWithResultAsync(
            resourceId,
            restrict,
            CancellationToken.None);
        if (result.Succeeded)
        {
            var label = restrict ? "enabled" : "disabled";
            SetSuccess($"Inherited access restriction {label}.");
        }
        else
        {
            SetError(result.ErrorMessage ?? "Failed to update inherited access setting.");
        }

        return RedirectToAction(nameof(Resources), new { slug });
    }

    [HttpPost("Resources/{resourceId}/Unlink")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnlinkResource(string slug, Guid resourceId)
    {
        var (currentUserNotFound, user) = await RequireCurrentUserAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team is null)
        {
            return NotFound();
        }

        if (!await CanManageResourcesAsync(team, user.Id))
        {
            return Forbid();
        }

        await _teamResourceService.UnlinkResourceAsync(resourceId);
        SetSuccess(_localizer["TeamAdmin_ResourceUnlinked"].Value);

        return RedirectToAction(nameof(Resources), new { slug });
    }

    [HttpPost("Resources/{resourceId}/Sync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncResource(string slug, Guid resourceId)
        => await SyncResourceCoreAsync(slug, resourceId);

    private async Task<IActionResult> SyncResourceCoreAsync(string slug, Guid resourceId)
    {
        var (currentUserNotFound, user) = await RequireCurrentUserAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team is null)
        {
            return NotFound();
        }

        if (!await CanManageResourcesAsync(team, user.Id))
        {
            return Forbid();
        }

        try
        {
            var diff = await _googleSyncService.SyncSingleResourceAsync(
                resourceId,
                SyncAction.Execute,
                HttpContext.RequestAborted);
            SetResourceSyncResult(diff.ErrorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing resource {ResourceId}", resourceId);
            SetError(string.Format(_localizer["TeamAdmin_ResourceSyncFailed"].Value, ex.Message));
        }

        return RedirectToAction(nameof(Resources), new { slug });
    }

    private void SetResourceSyncResult(string? syncError)
    {
        if (syncError is not null)
            SetError(string.Format(_localizer["TeamAdmin_ResourceSyncFailed"].Value, syncError));
        else
            SetSuccess(_localizer["TeamAdmin_ResourceSynced"].Value);
    }

    private void SetDriveResourceLinkResult(LinkResourceResult result)
    {
        if (result.Success)
        {
            SetSuccess($"Drive resource '{result.Resource!.Name}' linked successfully.");
            return;
        }

        SetError(BuildResourceLinkError(result, "Failed to link Drive resource."));
    }

    private void SetGroupResourceLinkResult(LinkResourceResult result)
    {
        if (result.Success)
        {
            SetSuccess(string.Format(_localizer["TeamAdmin_GroupLinked"].Value, result.Resource!.Name));
            return;
        }

        SetError(BuildResourceLinkError(result, _localizer["TeamAdmin_GroupLinkFailed"].Value));
    }

    private string BuildResourceLinkError(LinkResourceResult result, string defaultMessage)
    {
        var errorMessage = result.ErrorMessage ?? defaultMessage;
        if (result.ServiceAccountEmail is not null)
        {
            errorMessage += $" {string.Format(_localizer["TeamAdmin_ServiceAccount"].Value, result.ServiceAccountEmail)}";
        }

        return errorMessage;
    }

    [HttpGet("Roles")]
    public async Task<IActionResult> Roles(string slug)
    {
        var (teamError, _, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        var definitions = await _teamService.GetRoleDefinitionsAsync(team.Id);
        var teamInfo = await _teamService.GetTeamAsync(team.Id);
        var members = teamInfo?.Members
            .OrderBy(m => m.Role)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        var memberUserIds = members.Select(m => m.UserId).ToList();
        var memberInfos = await _userService.GetUserInfosAsync(memberUserIds);

        var canToggleManagement = RoleChecks.IsTeamsAdmin(User) || RoleChecks.IsAdmin(User);

        var teamMembers = members.Select(m => new TeamMemberViewModel
        {
            UserId = m.UserId,
            DisplayName = m.DisplayName,
            Email = m.Email ?? "",
            ProfilePictureUrl = memberInfos.GetValueOrDefault(m.UserId)?.ProfilePictureUrl,
            Role = m.Role,
            JoinedAt = m.JoinedAt.ToDateTimeUtc(),
            IsCoordinator = m.Role == TeamMemberRole.Coordinator
        }).ToList();

        var viewModel = new RoleManagementViewModel
        {
            TeamId = team.Id,
            TeamName = team.Name,
            Slug = team.Slug,
            IsSystemTeam = team.IsSystemTeam,
            IsChildTeam = team.ParentTeamId.HasValue,
            CanManage = true,
            CanToggleManagement = canToggleManagement,
            RoleDefinitions = definitions.Select(d => TeamRoleDefinitionViewModel.FromSnapshot(d, teamMembers)).ToList(),
            TeamMembers = teamMembers
        };

        return View(viewModel);
    }

    [HttpPost("Roles/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRole(string slug, CreateRoleDefinitionModel model)
    {
        var (teamError, user, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        try
        {
            var priorities = model.Priorities
                .Select(p => Enum.Parse<SlotPriority>(p, ignoreCase: true))
                .ToList();

            await _teamService.CreateRoleDefinitionAsync(
                team.Id, model.Name, model.Description, model.SlotCount,
                priorities, model.SortOrder, model.Period, user.Id, model.IsPublic);

            SetSuccess($"Role '{model.Name}' created.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to create role '{RoleName}' for team {TeamId} by user {UserId}", model.Name, team.Id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Roles), new { slug });
    }

    [HttpPost("Roles/{roleId}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRole(string slug, Guid roleId, EditRoleDefinitionModel model)
    {
        var isAjax = Request.Headers.XRequestedWith == "XMLHttpRequest";

        var (teamError, user, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return RoleEditTeamError(isAjax, teamError);
        }

        try
        {
            var canToggleManagement = RoleChecks.IsTeamsAdmin(User) || RoleChecks.IsAdmin(User);
            var priorities = model.Priorities
                .Select(p => Enum.Parse<SlotPriority>(p, ignoreCase: true))
                .ToList();

            await _teamService.UpdateRoleDefinitionAsync(
                roleId, model.Name, model.Description, model.SlotCount,
                priorities, model.SortOrder, model.IsManagement, model.Period, user.Id,
                model.IsPublic, canToggleManagement);

            return RoleEditSuccess(isAjax, slug, $"Role '{model.Name}' updated.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to update role {RoleId} for team {TeamId} by user {UserId}", roleId, team.Id, user.Id);
            return RoleEditError(isAjax, slug, ex.Message);
        }
    }

    private IActionResult RoleEditTeamError(bool isAjax, IActionResult teamError) =>
        isAjax ? Unauthorized() : teamError;

    private IActionResult RoleEditSuccess(bool isAjax, string slug, string message)
    {
        if (isAjax) return Json(new { success = true, message });
        SetSuccess(message);
        return RedirectToAction(nameof(Roles), new { slug });
    }

    private IActionResult RoleEditError(bool isAjax, string slug, string message)
    {
        if (isAjax) return Json(new { success = false, message });
        SetError(message);
        return RedirectToAction(nameof(Roles), new { slug });
    }

    [HttpPost("Roles/{roleId}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRole(string slug, Guid roleId)
    {
        var (teamError, user, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        try
        {
            await _teamService.DeleteRoleDefinitionAsync(roleId, user.Id);
            SetSuccess("Role deleted.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to delete role {RoleId} for team {TeamId} by user {UserId}", roleId, team.Id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Roles), new { slug });
    }

    [HttpPost("Roles/{roleId}/ToggleManagement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleManagement(string slug, Guid roleId)
    {
        var (teamError, user, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        if (!RoleChecks.IsTeamsAdmin(User) && !RoleChecks.IsAdmin(User))
        {
            return Forbid();
        }

        try
        {
            var result = await _teamService.ToggleRoleIsManagementAsync(roleId, user.Id);
            SetSuccess(result.IsManagement
                ? $"'{result.RoleName}' is now the management role. Members assigned to it will become Coordinators."
                : $"'{result.RoleName}' is no longer the management role.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to toggle management flag for role {RoleId} in team {TeamId} by user {UserId}", roleId, team.Id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Roles), new { slug });
    }

    [HttpPost("Roles/{roleId}/Assign")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignRole(string slug, Guid roleId, AssignRoleModel model)
    {
        var (teamError, user, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        try
        {
            await _teamService.AssignToRoleAsync(roleId, model.UserId, user.Id);
            await _systemTeamSyncJob.SyncMembershipForUserAsync(model.UserId, SystemTeamType.Coordinators);
            SetSuccess("Member assigned to role.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to assign member {MemberUserId} to role {RoleId} in team {TeamId} by user {UserId}", model.UserId, roleId, team.Id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Roles), new { slug });
    }

    [HttpPost("Roles/{roleId}/Unassign/{memberId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnassignRole(string slug, Guid roleId, Guid memberId)
    {
        var (teamError, user, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        try
        {
            var teamInfo = await _teamService.GetTeamAsync(team.Id);
            var member = teamInfo?.Members.FirstOrDefault(m => m.TeamMemberId == memberId);
            var userId = member?.UserId;

            await _teamService.UnassignFromRoleAsync(roleId, memberId, user.Id);

            if (userId.HasValue)
            {
                await _systemTeamSyncJob.SyncMembershipForUserAsync(userId.Value, SystemTeamType.Coordinators);
            }

            SetSuccess("Member unassigned from role.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to unassign member {MemberId} from role {RoleId} in team {TeamId} by user {UserId}", memberId, roleId, team.Id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Roles), new { slug });
    }

    [HttpGet("EditPage")]
    public async Task<IActionResult> EditPage(string slug)
    {
        var (teamError, _, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
            return teamError;

        var teamEntity = await _teamService.GetTeamByIdAsync(team.Id);
        if (teamEntity is null)
            return NotFound();

        var canBePublic = !teamEntity.IsSystemTeam && !teamEntity.ParentTeamId.HasValue;

        // Ensure we always show 3 CTA slots
        var ctas = (teamEntity.CallsToAction ?? [])
            .Select(c => new CallToActionViewModel { Text = c.Text, Url = c.Url, Style = c.Style })
            .ToList();
        while (ctas.Count < 3)
            ctas.Add(new CallToActionViewModel { Style = ctas.Count == 0 ? CallToActionStyle.Primary : CallToActionStyle.Secondary });

        var viewModel = new EditTeamPageViewModel
        {
            TeamId = teamEntity.Id,
            Slug = teamEntity.Slug,
            TeamName = teamEntity.DisplayName,
            IsPublicPage = teamEntity.IsPublicPage,
            ShowCoordinatorsOnPublicPage = teamEntity.ShowCoordinatorsOnPublicPage,
            CanBePublic = canBePublic,
            PageContent = teamEntity.PageContent,
            CallsToAction = ctas
        };

        return View(viewModel);
    }

    [HttpPost("EditPage")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditPage(string slug, EditTeamPageViewModel model)
    {
        var (teamError, user, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
            return teamError;

        if (!ModelState.IsValid)
        {
            var teamEntity = await _teamService.GetTeamByIdAsync(team.Id);
            if (teamEntity is null)
                return NotFound();
            PopulateEditTeamPageModel(model, teamEntity);
            return View(model);
        }

        var result = await _teamService.UpdateTeamPageContentAsync(
            team.Id,
            model.PageContent,
            model.CallsToAction
                .Select(c => new TeamPageCallToActionInput(c.Text, c.Url, c.Style))
                .ToList(),
            model.IsPublicPage,
            model.ShowCoordinatorsOnPublicPage,
            user.Id);

        if (result.Succeeded)
        {
            SetSuccess(_localizer["EditTeamPage_Saved"].Value);
            return RedirectToAction(nameof(TeamController.Details), "Team", new { slug });
        }

        ModelState.AddModelError("", result.ErrorMessage ?? "Failed to update team page.");
        var teamEntityAfterFailure = await _teamService.GetTeamByIdAsync(team.Id);
        if (teamEntityAfterFailure is null)
            return NotFound();
        PopulateEditTeamPageModel(model, teamEntityAfterFailure);
        return View(model);
    }

    [HttpGet("Roles/SearchMembers")]
    public async Task<IActionResult> SearchMembersForRole(string slug, string q)
    {
        var (teamError, _, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        if (!q.HasSearchTerm())
        {
            return Json(Array.Empty<RoleAssignmentSearchResult>());
        }

        var teamMembers = team.Members;
        var teamMemberUserIds = teamMembers
            .Select(m => m.UserId)
            .ToHashSet();

        var matchingTeamMembers = teamMembers
            .Where(m => m.DisplayName.ContainsOrdinalIgnoreCase(q) ||
                        (m.Email?.ContainsOrdinalIgnoreCase(q) ?? false))
            .Take(10)
            .Select(m => new RoleAssignmentSearchResult(m.UserId, m.DisplayName, m.Email ?? "", true))
            .ToList();

        // Name-only for role-picker (no bio/contact data); team admins are not global admins.
        var allResults = await _userService.SearchUsersAsync(
            q, PersonSearchFields.Name, limit: 50);
        var nonMembers = allResults
            .Where(r => !teamMemberUserIds.Contains(r.UserId))
            .OrderBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase)
            .Take(10 - matchingTeamMembers.Count)
            .Select(r => new RoleAssignmentSearchResult(r.UserId, r.BurnerName, "", false))
            .ToList();

        var combined = matchingTeamMembers.Concat(nonMembers).ToList();
        return Json(combined);
    }

    private async Task<bool> CanManageResourcesAsync(Team team, Guid userId)
    {
        if (RoleChecks.IsTeamsAdminBoardOrAdmin(User))
            return true;

        // Sub-team managers cannot manage Google resources — check at department level.
        var checkTeamId = team.ParentTeamId ?? team.Id;
        return await _teamResourceService.CanManageTeamResourcesAsync(checkTeamId, userId);
    }

    private static void PopulateEditTeamPageModel(EditTeamPageViewModel model, Team team)
    {
        model.Slug = team.Slug;
        model.TeamName = team.DisplayName;
        model.CanBePublic = !team.IsSystemTeam && !team.ParentTeamId.HasValue;
    }
}

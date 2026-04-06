using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using Humans.Application.Interfaces;
using Humans.Web.Authorization;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Humans.Web.Extensions;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Teams/{slug}")]
public class TeamAdminController : HumansTeamControllerBase
{
    private readonly ITeamService _teamService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IProfileService _profileService;
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
        IEmailProvisioningService emailProvisioningService,
        INotificationService notificationService,
        UserManager<User> userManager,
        ISystemTeamSync systemTeamSyncJob,
        IMemoryCache cache,
        ILogger<TeamAdminController> logger,
        IStringLocalizer<SharedResource> localizer)
        : base(userManager, teamService)
    {
        _teamService = teamService;
        _teamResourceService = teamResourceService;
        _googleSyncService = googleSyncService;
        _profileService = profileService;
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
            var newMember = await _teamService.ApproveJoinRequestAsync(requestId, user.Id, model.Notes);
            SetSuccess(_localizer["TeamAdmin_RequestApproved"].Value);

            // Notify requester (best-effort)
            try
            {
                await _notificationService.SendAsync(
                    NotificationSource.TeamJoinRequestDecided,
                    NotificationClass.Informational,
                    NotificationPriority.Normal,
                    $"Your request to join {team.Name} has been approved",
                    [newMember.UserId],
                    body: $"Welcome to {team.Name}!",
                    actionUrl: $"/Teams/{slug}",
                    actionLabel: "View team");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch TeamJoinRequestDecided notification for request {RequestId}", requestId);
            }
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

        // Look up requester before rejection (service consumes the request)
        var pendingRequests = await _teamService.GetPendingRequestsForTeamAsync(team.Id);
        var request = pendingRequests.FirstOrDefault(r => r.Id == requestId);
        var requesterUserId = request?.UserId;

        try
        {
            await _teamService.RejectJoinRequestAsync(requestId, user.Id, model.Notes);
            SetSuccess(_localizer["TeamAdmin_RequestRejected"].Value);

            // Notify requester (best-effort)
            if (requesterUserId.HasValue)
            {
                try
                {
                    await _notificationService.SendAsync(
                        NotificationSource.TeamJoinRequestDecided,
                        NotificationClass.Informational,
                        NotificationPriority.Normal,
                        $"Your request to join {team.Name} was not approved",
                        [requesterUserId.Value],
                        body: $"Your request to join {team.Name} was not approved.",
                        actionUrl: "/Teams",
                        actionLabel: "Browse teams");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to dispatch TeamJoinRequestDecided notification for request {RequestId}", requestId);
                }
            }
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

        var allMembers = await _teamService.GetTeamMembersAsync(team.Id);
        var totalCount = allMembers.Count;

        var pagedMembers = allMembers
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var memberUserIds = pagedMembers.Select(m => m.UserId).ToList();
        var profilesWithCustomPictures = await _profileService.GetCustomPictureInfoByUserIdsAsync(memberUserIds);
        var customPictureByUserId = profilesWithCustomPictures.ToDictionary(
            p => p.UserId,
            p => Url.Action(nameof(ProfileController.Picture), "Profile", new { id = p.ProfileId, v = p.UpdatedAtTicks })!);

        // nobodies.team email is now resolved by NobodiesEmailBadgeViewComponent in the view
        var members = pagedMembers
            .Select(m => new TeamMemberViewModel
            {
                UserId = m.UserId,
                DisplayName = m.User.DisplayName,
                Email = m.User.Email ?? "",
                ProfilePictureUrl = m.User.ProfilePictureUrl,
                HasCustomProfilePicture = customPictureByUserId.ContainsKey(m.UserId),
                CustomProfilePictureUrl = customPictureByUserId.GetValueOrDefault(m.UserId),
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
                UserDisplayName = r.User.DisplayName,
                UserEmail = r.User.Email ?? "",
                UserProfilePictureUrl = r.User.ProfilePictureUrl,
                Status = r.Status,
                Message = r.Message,
                RequestedAt = r.RequestedAt.ToDateTimeUtc()
            }).ToList();

        // Load Google resources for the resource access summary card
        var allTeamResources = await _teamResourceService.GetTeamResourcesAsync(team.Id);
        var teamResources = allTeamResources.Where(r => r.IsActive).OrderBy(r => r.ResourceType).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();

        var parentDepartmentResources = new List<GoogleResource>();
        string? parentDepartmentName = null;
        string? parentDepartmentSlug = null;
        if (team.ParentTeam is not null)
        {
            parentDepartmentName = team.ParentTeam.Name;
            parentDepartmentSlug = team.ParentTeam.CustomSlug ?? team.ParentTeam.Slug;
            var allParentResources = await _teamResourceService.GetTeamResourcesAsync(team.ParentTeam.Id);
            parentDepartmentResources = allParentResources.Where(r => r.IsActive).OrderBy(r => r.ResourceType).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        }

        static ResourceAccessViewModel MapResource(GoogleResource r) => new()
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
            PendingRequests = pendingRequestViewModels,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize,
            TeamResources = teamResources.Select(MapResource).ToList(),
            ParentDepartmentResources = parentDepartmentResources.Select(MapResource).ToList(),
            ParentDepartmentName = parentDepartmentName,
            ParentDepartmentSlug = parentDepartmentSlug,
            IsSensitive = team.IsSensitive,
            ActorDisplayName = user.DisplayName
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
                await _systemTeamSyncJob.SyncCoordinatorsMembershipForUserAsync(userId);
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

        // Verify that the target user is actually a member of this team
        var teamMembers = await _teamService.GetTeamMembersAsync(team.Id);
        if (teamMembers.All(m => m.UserId != userId))
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
            // Evict the nobodies.team email cache so the ViewComponent reflects the new email immediately
            _cache.Remove(ViewComponents.NobodiesEmailBadgeViewComponent.CacheKey);

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
            return Json(Array.Empty<ApprovedUserSearchResult>());
        }

        var results = await _profileService.SearchApprovedUsersAsync(q);

        // Exclude existing team members
        var existingMemberIds = team.Members
            .Where(m => m.LeftAt is null)
            .Select(m => m.UserId)
            .ToHashSet();

        var filtered = results
            .Where(r => !existingMemberIds.Contains(r.UserId))
            .Take(10)
            .Select(r => r.ToApprovedUserSearchResult())
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

        if (result.Success)
        {
            SetSuccess($"Drive resource '{result.Resource!.Name}' linked successfully.");
        }
        else
        {
            var errorMessage = result.ErrorMessage ?? "Failed to link Drive resource.";
            if (result.ServiceAccountEmail is not null)
            {
                errorMessage += $" {string.Format(_localizer["TeamAdmin_ServiceAccount"].Value, result.ServiceAccountEmail)}";
            }
            SetError(errorMessage);
        }

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

        if (result.Success)
        {
            SetSuccess(string.Format(_localizer["TeamAdmin_GroupLinked"].Value, result.Resource!.Name));
        }
        else
        {
            var errorMessage = result.ErrorMessage ?? _localizer["TeamAdmin_GroupLinkFailed"].Value;
            if (result.ServiceAccountEmail is not null)
            {
                errorMessage += $" {string.Format(_localizer["TeamAdmin_ServiceAccount"].Value, result.ServiceAccountEmail)}";
            }
            SetError(errorMessage);
        }

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

        try
        {
            await _teamResourceService.SetRestrictInheritedAccessAsync(resourceId, restrict, CancellationToken.None);
            var label = restrict ? "enabled" : "disabled";
            SetSuccess($"Inherited access restriction {label}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle RestrictInheritedAccess for resource {ResourceId}", resourceId);
            SetError($"Failed to update inherited access setting: {ex.Message}");
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
            await _googleSyncService.SyncSingleResourceAsync(resourceId, SyncAction.Execute);
            SetSuccess(_localizer["TeamAdmin_ResourceSynced"].Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing resource {ResourceId}", resourceId);
            SetError(string.Format(_localizer["TeamAdmin_ResourceSyncFailed"].Value, ex.Message));
        }

        return RedirectToAction(nameof(Resources), new { slug });
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
        var members = await _teamService.GetTeamMembersAsync(team.Id);

        var memberUserIds = members.Select(m => m.UserId).ToList();
        var profilesWithCustomPictures = await _profileService.GetCustomPictureInfoByUserIdsAsync(memberUserIds);
        var customPictureByUserId = profilesWithCustomPictures.ToDictionary(
            p => p.UserId,
            p => Url.Action(nameof(ProfileController.Picture), "Profile", new { id = p.ProfileId, v = p.UpdatedAtTicks })!);

        var viewModel = new RoleManagementViewModel
        {
            TeamId = team.Id,
            TeamName = team.Name,
            Slug = team.Slug,
            IsSystemTeam = team.IsSystemTeam,
            IsChildTeam = team.ParentTeamId.HasValue,
            CanManage = true,
            RoleDefinitions = definitions.Select(TeamRoleDefinitionViewModel.FromEntity).ToList(),
            TeamMembers = members.Select(m => new TeamMemberViewModel
            {
                UserId = m.UserId,
                DisplayName = m.User.DisplayName,
                Email = m.User.Email ?? "",
                ProfilePictureUrl = m.User.ProfilePictureUrl,
                HasCustomProfilePicture = customPictureByUserId.ContainsKey(m.UserId),
                CustomProfilePictureUrl = customPictureByUserId.GetValueOrDefault(m.UserId),
                Role = m.Role,
                JoinedAt = m.JoinedAt.ToDateTimeUtc(),
                IsCoordinator = m.Role == TeamMemberRole.Coordinator
            }).ToList()
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
            _logger.LogDebug(ex, "Failed to create role '{RoleName}' for team {TeamId} by user {UserId}", model.Name, team.Id, user.Id);
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
            return isAjax ? Unauthorized() : teamError;
        }

        try
        {
            var priorities = model.Priorities
                .Select(p => Enum.Parse<SlotPriority>(p, ignoreCase: true))
                .ToList();

            await _teamService.UpdateRoleDefinitionAsync(
                roleId, model.Name, model.Description, model.SlotCount,
                priorities, model.SortOrder, model.IsManagement, model.Period, user.Id,
                model.IsPublic);

            var successMsg = $"Role '{model.Name}' updated.";
            if (isAjax) return Json(new { success = true, message = successMsg });
            SetSuccess(successMsg);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            _logger.LogDebug(ex, "Failed to update role {RoleId} for team {TeamId} by user {UserId}", roleId, team.Id, user.Id);
            if (isAjax) return Json(new { success = false, message = ex.Message });
            SetError(ex.Message);
        }

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
            _logger.LogDebug(ex, "Failed to delete role {RoleId} for team {TeamId} by user {UserId}", roleId, team.Id, user.Id);
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

        try
        {
            var roles = await _teamService.GetRoleDefinitionsAsync(team.Id);
            var role = roles.FirstOrDefault(r => r.Id == roleId);
            if (role is null)
            {
                return NotFound();
            }

            await _teamService.SetRoleIsManagementAsync(roleId, !role.IsManagement, user.Id);
            SetSuccess(role.IsManagement
                ? $"'{role.Name}' is no longer the management role."
                : $"'{role.Name}' is now the management role. Members assigned to it will become Coordinators.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            _logger.LogDebug(ex, "Failed to toggle management flag for role {RoleId} in team {TeamId} by user {UserId}", roleId, team.Id, user.Id);
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
            await _systemTeamSyncJob.SyncCoordinatorsMembershipForUserAsync(model.UserId);
            SetSuccess("Member assigned to role.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            _logger.LogDebug(ex, "Failed to assign member {MemberUserId} to role {RoleId} in team {TeamId} by user {UserId}", model.UserId, roleId, team.Id, user.Id);
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
            // Look up the member's UserId before unassigning
            var members = await _teamService.GetTeamMembersAsync(team.Id);
            var member = members.FirstOrDefault(m => m.Id == memberId);
            var userId = member?.UserId;

            await _teamService.UnassignFromRoleAsync(roleId, memberId, user.Id);

            if (userId.HasValue)
            {
                await _systemTeamSyncJob.SyncCoordinatorsMembershipForUserAsync(userId.Value);
            }

            SetSuccess("Member unassigned from role.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            _logger.LogDebug(ex, "Failed to unassign member {MemberId} from role {RoleId} in team {TeamId} by user {UserId}", memberId, roleId, team.Id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Roles), new { slug });
    }

    [HttpGet("EditPage")]
    public async Task<IActionResult> EditPage(string slug)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team is null)
            return NotFound();

        var isCoordinator = await _teamService.IsUserCoordinatorOfTeamAsync(team.Id, user.Id);
        var canManage = isCoordinator || RoleChecks.IsTeamsAdminBoardOrAdmin(User);
        if (!canManage)
            return Forbid();

        var canBePublic = !team.IsSystemTeam && !team.ParentTeamId.HasValue;

        // Ensure we always show 3 CTA slots
        var ctas = (team.CallsToAction ?? [])
            .Select(c => new CallToActionViewModel { Text = c.Text, Url = c.Url, Style = c.Style })
            .ToList();
        while (ctas.Count < 3)
            ctas.Add(new CallToActionViewModel { Style = ctas.Count == 0 ? CallToActionStyle.Primary : CallToActionStyle.Secondary });

        var viewModel = new EditTeamPageViewModel
        {
            TeamId = team.Id,
            Slug = team.Slug,
            TeamName = team.DisplayName,
            IsPublicPage = team.IsPublicPage,
            ShowCoordinatorsOnPublicPage = team.ShowCoordinatorsOnPublicPage,
            CanBePublic = canBePublic,
            PageContent = team.PageContent,
            CallsToAction = ctas
        };

        return View(viewModel);
    }

    [HttpPost("EditPage")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditPage(string slug, EditTeamPageViewModel model)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team is null)
            return NotFound();

        var isCoordinator = await _teamService.IsUserCoordinatorOfTeamAsync(team.Id, user.Id);
        var canManage = isCoordinator || RoleChecks.IsTeamsAdminBoardOrAdmin(User);
        if (!canManage)
            return Forbid();

        if (!ModelState.IsValid)
        {
            model.Slug = team.Slug;
            model.TeamName = team.DisplayName;
            model.CanBePublic = !team.IsSystemTeam && !team.ParentTeamId.HasValue;
            return View(model);
        }

        // Convert view model CTAs to domain, filtering out empty ones
        var callsToAction = model.CallsToAction
            .Where(c => !string.IsNullOrWhiteSpace(c.Text) && !string.IsNullOrWhiteSpace(c.Url))
            .Select(c => new CallToAction { Text = c.Text!.Trim(), Url = c.Url!.Trim(), Style = c.Style })
            .ToList();

        try
        {
            await _teamService.UpdateTeamPageContentAsync(
                team.Id,
                model.PageContent,
                callsToAction,
                model.IsPublicPage,
                model.ShowCoordinatorsOnPublicPage,
                user.Id);

            SetSuccess(_localizer["EditTeamPage_Saved"].Value);
            return RedirectToAction("Details", "Team", new { slug });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to update team page for team {TeamId} by user {UserId}", team.Id, user.Id);
            ModelState.AddModelError("", ex.Message);
            model.Slug = team.Slug;
            model.TeamName = team.DisplayName;
            model.CanBePublic = !team.IsSystemTeam && !team.ParentTeamId.HasValue;
            return View(model);
        }
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

        var teamMembers = await _teamService.GetTeamMembersAsync(team.Id);
        var teamMemberUserIds = teamMembers
            .Where(m => m.LeftAt is null)
            .Select(m => m.UserId)
            .ToHashSet();

        // Search team members first by name match
        var matchingTeamMembers = teamMembers
            .Where(m => m.LeftAt is null &&
                        (m.User.DisplayName.ContainsOrdinalIgnoreCase(q) ||
                         m.User.Email.ContainsOrdinalIgnoreCase(q)))
            .Take(10)
            .Select(m => new RoleAssignmentSearchResult(m.UserId, m.User.DisplayName, m.User.Email ?? "", true))
            .ToList();

        // Also search all approved users for non-members
        var allResults = await _profileService.SearchApprovedUsersAsync(q);
        var nonMembers = allResults
            .Where(r => !teamMemberUserIds.Contains(r.UserId))
            .Take(10 - matchingTeamMembers.Count)
            .Select(r => new RoleAssignmentSearchResult(r.UserId, r.DisplayName, r.Email, false))
            .ToList();

        var combined = matchingTeamMembers.Concat(nonMembers).ToList();
        return Json(combined);
    }

    private async Task<bool> CanManageResourcesAsync(Team team, Guid userId)
    {
        // Claims-first for global roles; DB only for team-specific coordinator check
        if (RoleChecks.IsTeamsAdminBoardOrAdmin(User))
            return true;

        // Sub-team managers cannot manage Google resources — check at department level
        var checkTeamId = team.ParentTeamId ?? team.Id;
        return await _teamResourceService.CanManageTeamResourcesAsync(checkTeamId, userId);
    }
}

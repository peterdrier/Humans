using Humans.GoogleIntegration.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Teams.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Humans.Tickets.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Models;
using Humans.Teams.Models;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Authorization;
using NodaTime.Text;

namespace Humans.Teams.Controllers;

[Authorize]
[Route("Teams/{slug}")]
internal sealed class TeamAdminController(
    ITeamManagementService teamService,
    ITeamResourceService teamResourceService,
    IUserService userService,
    IEmailProvisioningService emailProvisioningService,
    IAuthorizationService authorizationService,
    ILogger<TeamAdminController> logger,
    IStringLocalizer<TeamsResource> localizer,
    ITicketService tickets)
    : HumansTeamControllerBase(userService, teamService, authorizationService)
{
    private readonly ITeamManagementService _teamService = teamService;
    private readonly IUserService _userService = userService;
    private readonly ITicketService _tickets = tickets;

    [HttpPost("Requests/{requestId}/Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveRequest(string slug, Guid requestId, ApproveRejectRequestModel model)
    {
        var (teamError, user, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected invalid join request approval form for request {RequestId} in team {TeamId} by user {UserId}",
                requestId, team.Id, user.Id);
            return BadRequest(ModelState);
        }

        var result = await _teamService.ApproveJoinRequestAsync(requestId, user.Id, model.Notes);
        if (result.Succeeded)
        {
            SetSuccess(localizer["Teams_TeamAdmin_RequestApproved"].Value);
        }
        else
        {
            logger.LogWarning("Failed to approve join request {RequestId} for team {TeamId} by user {UserId}: {Reason}",
                requestId, team.Id, user.Id, result.Refusal);
            SetError(result.Refusal!);
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

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected invalid join request rejection form for request {RequestId} in team {TeamId} by user {UserId}",
                requestId, team.Id, user.Id);
            return BadRequest(ModelState);
        }

        if (string.IsNullOrWhiteSpace(model.Notes))
        {
            SetError(localizer["Teams_TeamAdmin_ProvideRejectionReason"].Value);
            return RedirectToAction(nameof(Members), new { slug });
        }

        var result = await _teamService.RejectJoinRequestAsync(requestId, user.Id, model.Notes!);
        if (result.Succeeded)
        {
            SetSuccess(localizer["Teams_TeamAdmin_RequestRejected"].Value);
        }
        else
        {
            logger.LogWarning("Failed to reject join request {RequestId} for team {TeamId} by user {UserId}: {Reason}",
                requestId, team.Id, user.Id, result.Refusal);
            SetError(result.Refusal!);
        }

        return RedirectToAction(nameof(Members), new { slug });
    }

    [HttpGet("Members")]
    public async Task<IActionResult> Members(string slug, int page = 1)
    {
        if (page < 1) page = 1;
        var pageSize = 20;
        var (teamError, user, team, canManage) = await ResolveTeamViewAsync(slug);
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
            .Skip((int)Math.Min(((long)page - 1) * pageSize, totalCount))
            .Take(pageSize)
            .ToList();

        var pagedMemberInfos = await _userService.GetUserInfosAsync(pagedMembers.Select(m => m.UserId).ToList());

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

        var pendingRequests = await _teamService.GetPendingRequestsForTeamAsync(team.Id);
        var pendingRequestViewModels = pendingRequests
            .Select(r => new TeamJoinRequestViewModel
            {
                Id = r.Id,
                TeamId = r.TeamId,
                TeamName = team.Name,
                UserId = r.UserId,
                UserEmail = r.UserEmail ?? "",
                Status = r.Status,
                Message = r.Message,
                RequestedAt = r.RequestedAt.ToDateTimeUtc()
            }).ToList();

        var allTeamResources = await teamResourceService.GetTeamResourcesAsync(team.Id);
        var teamResources = allTeamResources.Where(r => r.IsActive).OrderBy(r => r.ResourceType).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();

        var parentDepartmentResources = new List<GoogleResourceSnapshot>();
        string? parentDepartmentName = null;
        string? parentDepartmentSlug = null;
        if (team.ParentTeamId is { } parentTeamId &&
            await _teamService.GetTeamAsync(parentTeamId) is { } parentTeam)
        {
            parentDepartmentName = parentTeam.Name;
            parentDepartmentSlug = parentTeam.CustomSlug ?? parentTeam.Slug;
            var allParentResources = await teamResourceService.GetTeamResourcesAsync(parentTeam.Id);
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
            ParentDepartmentSlug = parentDepartmentSlug,
            IsSensitive = team.IsSensitive,
            ActorUserId = user.Id
        };

        return View(viewModel);
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

    /// <summary>
    /// The policy narrows <see cref="HumansTeamControllerBase.ResolveTeamManagementAsync"/>, which also passes
    /// coordinators, to Board or Admin.
    /// </summary>
    [HttpGet("Roster")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    public async Task<IActionResult> Roster(string slug, CancellationToken ct)
    {
        var (teamError, _, team) = await ResolveTeamManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        var members = team.Members;
        var infos = await _userService.GetUserInfosAsync(
            members.Select(m => m.UserId).ToList(), ct);

        // The table's headers re-sort client-side from here.
        var rows = members
            .Select(m => new TeamRosterRowViewModel(
                m.UserId,
                infos.GetValueOrDefault(m.UserId)?.Profile?.FullName ?? string.Empty,
                m.JoinedAt))
            .OrderBy(r => r.LegalName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return View(new TeamRosterViewModel(team.Name, team.Slug, rows));
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

        var result = await _teamService.RemoveMemberAsync(team.Id, userId, user.Id);
        if (result.ErrorKey is { } errorKey)
        {
            logger.LogWarning("Refused member removal {MemberUserId} from team {TeamId} by user {UserId}: {ErrorKey}", userId, team.Id, user.Id, errorKey);
            SetError(localizer[errorKey].Value);
        }
        else
            SetSuccess(localizer["Teams_TeamAdmin_MemberRemoved"].Value);

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

        // google_sync_outbox's FK rejects Guid.Empty.
        if (model.UserId == Guid.Empty)
        {
            SetError("Select a user to add.");
            return RedirectToAction(nameof(Members), new { slug });
        }

        var result = await _teamService.AddMemberToTeamAsync(team.Id, model.UserId, user.Id);
        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Refused member addition {MemberUserId} to team {TeamId} by user {UserId}: {Reason}", model.UserId, team.Id, user.Id, refusal);
            SetError(refusal);
        }
        else
            SetSuccess(localizer["Teams_TeamAdmin_MemberAdded"].Value);

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

        var result = await emailProvisioningService.ProvisionNobodiesEmailAsync(
            userId, emailPrefix, user.Id);

        if (!result.Success)
        {
            SetError(result.ErrorMessage ?? "Provisioning failed.");
        }
        else
        {

            if (result.WarningMessage is not null)
            {
                SetInfo(result.WarningMessage);
            }
            else
            {
                SetSuccess($"Account {result.FullEmail} provisioned and linked. Credentials email queued for {result.RecoveryEmail}.");
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

        // Name-only: team admins aren't global admins, so no contact-data search. Uncapped and relevance-ranked, so the
        // target can't fall outside an arbitrary cap.
        var results = await _userService.SearchUsersAsync(
            q, PersonSearchFields.Name, limit: int.MaxValue);

        var teamInfo = await _teamService.GetTeamAsync(team.Id);
        var existingMemberIds = teamInfo?.Members.Select(m => m.UserId).ToHashSet() ?? [];

        var filtered = results
            .Where(r => !existingMemberIds.Contains(r.UserId))
            .OrderByRelevance()
            .Select(r => new HumanLookupSearchResult(r.UserId, r.BurnerName))
            .ToList();

        return Json(filtered);
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

        var canToggleManagement = (await AuthorizationService.AuthorizeAsync(User, TeamsPolicies.Manage)).Succeeded;

        var teamMembers = members.Select(m => new TeamMemberViewModel
        {
            UserId = m.UserId,
            Email = m.Email ?? "",
            Role = m.Role,
            JoinedAt = m.JoinedAt.ToDateTimeUtc(),
            IsCoordinator = m.Role == TeamMemberRole.Coordinator
        }).ToList();

        var memberOptions = members
            .Select(m => new TeamMemberDropdownItem
            {
                UserId = m.UserId,
                BurnerName = memberInfos.TryGetValue(m.UserId, out var info) ? info.BurnerName : string.Empty
            })
            .OrderBy(o => o.BurnerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
            TeamMembers = teamMembers,
            MemberOptions = memberOptions
        };

        return View(viewModel);
    }

    private static List<SlotPriority>? ParseRolePriorities(IEnumerable<string> values)
    {
        var priorities = new List<SlotPriority>();
        foreach (var value in values)
        {
            if (!Enum.TryParse<SlotPriority>(value, ignoreCase: true, out var priority))
                return null;
            priorities.Add(priority);
        }
        return priorities;
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

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed role creation for team {TeamId} by user {UserId}", team.Id, user.Id);
            return BadRequest(ModelState);
        }

        var priorities = ParseRolePriorities(model.Priorities);
        var result = priorities is null
            ? new TeamRoleDefinitionMutationResult(Refusal: "Each slot priority must be a defined priority value")
            : await _teamService.CreateRoleDefinitionAsync(
                team.Id, model.Name, model.Description, model.SlotCount,
                priorities, model.SortOrder, model.Period, user.Id, model.IsPublic,
                model.EstimatedHours);

        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Role creation refused for team {TeamId} by user {UserId}: {Reason}", team.Id, user.Id, refusal);
            SetError(refusal);
        }
        else
        {
            SetSuccess($"Role '{model.Name}' created.");
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

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed role update for team {TeamId} by user {UserId}", team.Id, user.Id);
            return BadRequest(ModelState);
        }

        var canToggleManagement = (await AuthorizationService.AuthorizeAsync(User, TeamsPolicies.Manage)).Succeeded;
        var priorities = ParseRolePriorities(model.Priorities);
        var result = priorities is null
            ? new TeamRoleDefinitionMutationResult(Refusal: "Each slot priority must be a defined priority value")
            : await _teamService.UpdateRoleDefinitionAsync(
                roleId, model.Name, model.Description, model.SlotCount,
                priorities, model.SortOrder, model.IsManagement, model.Period, user.Id,
                model.IsPublic, canToggleManagement, model.EstimatedHours);

        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Role update refused for role {RoleId} in team {TeamId} by user {UserId}: {Reason}",
                roleId, team.Id, user.Id, refusal);
            if (isAjax)
                return Json(new { success = false, message = refusal });

            SetError(refusal);
        }
        else
        {
            if (isAjax)
                return Json(new { success = true, message = $"Role '{model.Name}' updated." });

            SetSuccess($"Role '{model.Name}' updated.");
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

        var result = await _teamService.DeleteRoleDefinitionAsync(roleId, user.Id);
        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Role deletion refused for role {RoleId} in team {TeamId} by user {UserId}: {Reason}",
                roleId, team.Id, user.Id, refusal);
            SetError(refusal);
        }
        else
        {
            SetSuccess("Role deleted.");
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

        if (!(await AuthorizationService.AuthorizeAsync(User, TeamsPolicies.Manage)).Succeeded)
        {
            return Forbid();
        }

        var result = await _teamService.ToggleRoleIsManagementAsync(roleId, user.Id);
        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Management-role toggle refused for role {RoleId} in team {TeamId} by user {UserId}: {Reason}",
                roleId, team.Id, user.Id, refusal);
            SetError(refusal);
        }
        else
        {
            var role = result.Definition!;
            SetSuccess(role.IsManagement
                ? $"'{role.Name}' is now the management role. Members assigned to it will become Coordinators."
                : $"'{role.Name}' is no longer the management role.");
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
            SetSuccess("Member assigned to role.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            logger.LogWarning(ex, "Failed to assign member {MemberUserId} to role {RoleId} in team {TeamId} by user {UserId}", model.UserId, roleId, team.Id, user.Id);
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
            await _teamService.UnassignFromRoleAsync(roleId, memberId, user.Id);
            SetSuccess("Member unassigned from role.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            logger.LogWarning(ex, "Failed to unassign member {MemberId} from role {RoleId} in team {TeamId} by user {UserId}", memberId, roleId, team.Id, user.Id);
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

        var ctas = (team.CallsToAction ?? [])
            .Select(c => new CallToActionViewModel { Text = c.Text, Url = c.Url, Style = c.Style })
            .ToList();
        while (ctas.Count < 3)
            ctas.Add(new CallToActionViewModel { Style = ctas.Count == 0 ? CallToActionStyle.Primary : CallToActionStyle.Secondary });

        var viewModel = new EditTeamPageViewModel
        {
            TeamId = team.Id,
            Slug = team.Slug,
            TeamName = await GetTeamDisplayNameAsync(team),
            IsPublicPage = team.IsPublicPage,
            ShowCoordinatorsOnPublicPage = team.ShowCoordinatorsOnPublicPage,
            CanBePublic = team.CanBePublic,
            PageContent = team.PageContent,
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
            await PopulateEditTeamPageModelAsync(model, team);
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
            SetSuccess(localizer["Teams_EditTeamPage_Saved"].Value);
            return RedirectToAction(nameof(TeamController.Details), "Team", new { slug });
        }

        ModelState.AddModelError("", result.ErrorMessage ?? "Failed to update team page.");
        await PopulateEditTeamPageModelAsync(model, team);
        return View(model);
    }

    [HttpGet("EarlyEntry")]
    public async Task<IActionResult> EarlyEntry(string slug, CancellationToken ct)
    {
        var (teamError, _, team) = await ResolveEarlyEntryManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        if (!team.EarlyEntryEnabled)
        {
            return NotFound();
        }

        var vm = await BuildEarlyEntryPageAsync(team, ct);
        return View(vm);
    }

    [HttpPost("EarlyEntry/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddEarlyEntry(string slug, AddTeamEarlyEntryInput input, CancellationToken ct)
    {
        var (teamError, user, team) = await ResolveEarlyEntryManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        if (!team.EarlyEntryEnabled)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
            return View(nameof(EarlyEntry), await BuildEarlyEntryPageAsync(team, ct));

        var parsed = LocalDatePattern.Iso.Parse(input.EntryDate);
        if (!parsed.Success)
        {
            ModelState.AddModelError(nameof(input.EntryDate), "Enter a valid date (yyyy-MM-dd).");
            return View(nameof(EarlyEntry), await BuildEarlyEntryPageAsync(team, ct));
        }

        try
        {
            await _teamService.AddEarlyEntryGrantAsync(
                team.Id, input.UserId, parsed.Value, input.ProjectName, user.Id, ct);
            SetSuccess("Early entry granted.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            logger.LogWarning(ex, "Failed to grant early entry for team {TeamId} by user {UserId}", team.Id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(EarlyEntry), new { slug });
    }

    [HttpPost("EarlyEntry/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditEarlyEntry(string slug, EditTeamEarlyEntryInput input, CancellationToken ct)
    {
        var (teamError, user, team) = await ResolveEarlyEntryManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        if (!team.EarlyEntryEnabled)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
            return View(nameof(EarlyEntry), await BuildEarlyEntryPageAsync(team, ct));

        var parsed = LocalDatePattern.Iso.Parse(input.EntryDate);
        if (!parsed.Success)
        {
            ModelState.AddModelError(nameof(input.EntryDate), "Enter a valid date (yyyy-MM-dd).");
            return View(nameof(EarlyEntry), await BuildEarlyEntryPageAsync(team, ct));
        }

        try
        {
            await _teamService.EditEarlyEntryGrantAsync(
                team.Id, input.GrantId, parsed.Value, input.ProjectName, user.Id, ct);
            SetSuccess("Early entry updated.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            logger.LogWarning(ex, "Failed to update early entry grant {GrantId} for team {TeamId} by user {UserId}", input.GrantId, team.Id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(EarlyEntry), new { slug });
    }

    [HttpPost("EarlyEntry/Remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveEarlyEntry(string slug, Guid grantId, CancellationToken ct)
    {
        var (teamError, user, team) = await ResolveEarlyEntryManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        if (!team.EarlyEntryEnabled)
        {
            return NotFound();
        }

        try
        {
            await _teamService.RemoveEarlyEntryGrantAsync(team.Id, grantId, user.Id, ct);
            SetSuccess("Early entry revoked.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or ArgumentException)
        {
            logger.LogWarning(ex, "Failed to revoke early entry grant {GrantId} for team {TeamId} by user {UserId}", grantId, team.Id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(EarlyEntry), new { slug });
    }

    [HttpGet("EarlyEntry/LookupTicket")]
    public async Task<IActionResult> LookupTicket(string slug, string? q, CancellationToken ct)
    {
        var (teamError, _, team) = await ResolveEarlyEntryManagementAsync(slug);
        if (teamError is not null)
        {
            return teamError;
        }

        if (!team.EarlyEntryEnabled)
        {
            return NotFound();
        }

        var hit = await _tickets.FindCurrentEventAttendeeByBarcodeAsync(q, ct);

        var matched = hit?.MatchedUserId is { } id
            ? await _userService.GetUserInfoAsync(id, ct)
            : null;

        var detailLabel = localizer["Teams_TeamAdmin_TicketLabel", hit?.Barcode ?? string.Empty].Value;
        return Json(BuildTicketLookupRows(hit, matched, detailLabel));
    }

    private async Task<TeamEarlyEntryPageViewModel> BuildEarlyEntryPageAsync(TeamInfo team, CancellationToken ct)
    {
        var grants = await _teamService.GetEarlyEntryGrantsForTeamAsync(team.Id, ct);
        var humans = await _userService.GetUserInfosAsync(
            grants.Select(g => g.UserId).Distinct().ToList(), ct);

        var rows = grants
            .Select(g => new TeamEarlyEntryRowViewModel
            {
                GrantId = g.Id,
                HumanName = humans.GetValueOrDefault(g.UserId)?.BurnerName ?? "",
                EntryDate = g.EntryDate,
                ProjectName = g.ProjectName,
            })
            .OrderBy(r => r.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.EntryDate)
            .ToList();

        return new TeamEarlyEntryPageViewModel
        {
            Slug = team.Slug,
            TeamName = team.Name,
            Grants = rows,
        };
    }

    /// <summary>
    /// A row only when the attendee is paired to a human who still resolves and is active; otherwise empty, so the
    /// picker shows only name matches.
    /// </summary>
    internal static List<HumanLookupSearchResult> BuildTicketLookupRows(
        TicketAttendeeInfo? hit, UserInfo? matchedUser, string detailLabel)
    {
        if (hit?.MatchedUserId is null || matchedUser is null || !matchedUser.IsActive)
        {
            return [];
        }

        return
        [
            new HumanLookupSearchResult(
                matchedUser.Id, matchedUser.BurnerName, detailLabel, matchedUser.ProfilePictureUrl),
        ];
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

        // Name-only (team admins aren't global admins); uncapped, folded and relevance-ranked.
        var allResults = await _userService.SearchUsersAsync(
            q, PersonSearchFields.Name, limit: int.MaxValue);
        var nameMatchIds = allResults.Select(r => r.UserId).ToHashSet();

        // Team members first, matched by folded name (profiled members), by DisplayName (legacy members without a
        // searchable profile), or by email: admins may know their own members' emails, which the global name-only
        // search won't surface for privacy.
        var matchingTeamMembers = teamMembers
            .Where(m => nameMatchIds.Contains(m.UserId) ||
                        m.DisplayName.ContainsOrdinalIgnoreCase(q) ||
                        (m.Email?.ContainsOrdinalIgnoreCase(q) ?? false))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(m => new RoleAssignmentSearchResult(m.UserId, m.DisplayName, m.Email ?? "", true))
            .ToList();

        var nonMembers = allResults
            .Where(r => !teamMemberUserIds.Contains(r.UserId))
            .OrderByRelevance()
            .Select(r => new RoleAssignmentSearchResult(r.UserId, r.BurnerName, "", false))
            .ToList();

        var combined = matchingTeamMembers.Concat(nonMembers).ToList();
        return Json(combined);
    }

    private async Task PopulateEditTeamPageModelAsync(EditTeamPageViewModel model, TeamInfo team)
    {
        model.Slug = team.Slug;
        model.TeamName = await GetTeamDisplayNameAsync(team);
        model.CanBePublic = team.CanBePublic;
    }

    private async Task<string> GetTeamDisplayNameAsync(TeamInfo team)
    {
        var parent = team.ParentTeamId is { } parentId
            ? await _teamService.GetTeamAsync(parentId, HttpContext.RequestAborted)
            : null;
        return parent is not null ? $"{parent.Name} - {team.Name}" : team.Name;
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Profiles.Application.Interfaces;
using Profiles.Domain.Entities;
using Profiles.Domain.Enums;
using Profiles.Web.Models;

namespace Profiles.Web.Controllers;

[Authorize]
[Route("Teams/{slug}/Admin")]
public class TeamAdminController : Controller
{
    private readonly ITeamService _teamService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly UserManager<User> _userManager;
    private readonly ILogger<TeamAdminController> _logger;

    public TeamAdminController(
        ITeamService teamService,
        ITeamResourceService teamResourceService,
        IGoogleSyncService googleSyncService,
        UserManager<User> userManager,
        ILogger<TeamAdminController> logger)
    {
        _teamService = teamService;
        _teamResourceService = teamResourceService;
        _googleSyncService = googleSyncService;
        _userManager = userManager;
        _logger = logger;
    }

    [HttpGet("Requests")]
    public async Task<IActionResult> Requests(string slug, int page = 1)
    {
        var pageSize = 20;
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return NotFound();
        }

        var canManage = await _teamService.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);
        if (!canManage)
        {
            return Forbid();
        }

        var allRequests = await _teamService.GetPendingRequestsForTeamAsync(team.Id);
        var totalCount = allRequests.Count;

        var requests = allRequests
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new TeamJoinRequestViewModel
            {
                Id = r.Id,
                TeamId = r.TeamId,
                TeamName = team.Name,
                UserId = r.UserId,
                UserDisplayName = r.User?.DisplayName ?? "Unknown",
                UserEmail = r.User?.Email ?? "",
                UserProfilePictureUrl = r.User?.ProfilePictureUrl,
                Status = r.Status.ToString(),
                Message = r.Message,
                RequestedAt = r.RequestedAt.ToDateTimeUtc()
            }).ToList();

        var viewModel = new PendingRequestsViewModel
        {
            TeamIdFilter = team.Id,
            TeamNameFilter = team.Name,
            Requests = requests,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        ViewData["TeamSlug"] = slug;
        ViewData["TeamName"] = team.Name;
        return View(viewModel);
    }

    [HttpPost("Requests/{requestId}/Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveRequest(string slug, Guid requestId, ApproveRejectRequestModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return NotFound();
        }

        var canManage = await _teamService.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);
        if (!canManage)
        {
            return Forbid();
        }

        try
        {
            await _teamService.ApproveJoinRequestAsync(requestId, user.Id, model.Notes);
            TempData["SuccessMessage"] = "Request approved. The user is now a team member.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Requests), new { slug });
    }

    [HttpPost("Requests/{requestId}/Reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectRequest(string slug, Guid requestId, ApproveRejectRequestModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return NotFound();
        }

        var canManage = await _teamService.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);
        if (!canManage)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(model.Notes))
        {
            TempData["ErrorMessage"] = "Please provide a reason for rejection.";
            return RedirectToAction(nameof(Requests), new { slug });
        }

        try
        {
            await _teamService.RejectJoinRequestAsync(requestId, user.Id, model.Notes);
            TempData["SuccessMessage"] = "Request rejected.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Requests), new { slug });
    }

    [HttpGet("Members")]
    public async Task<IActionResult> Members(string slug, int page = 1)
    {
        var pageSize = 20;
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return NotFound();
        }

        var canManage = await _teamService.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);
        if (!canManage)
        {
            return Forbid();
        }

        var allMembers = await _teamService.GetTeamMembersAsync(team.Id);
        var totalCount = allMembers.Count;

        var members = allMembers
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new TeamMemberViewModel
            {
                UserId = m.UserId,
                DisplayName = m.User?.DisplayName ?? "Unknown",
                Email = m.User?.Email ?? "",
                ProfilePictureUrl = m.User?.ProfilePictureUrl,
                Role = m.Role.ToString(),
                JoinedAt = m.JoinedAt.ToDateTimeUtc(),
                IsMetalead = m.Role == TeamMemberRole.Metalead
            }).ToList();

        var viewModel = new TeamMembersViewModel
        {
            TeamId = team.Id,
            TeamName = team.Name,
            TeamSlug = team.Slug,
            IsSystemTeam = team.IsSystemTeam,
            CanManageRoles = !team.IsSystemTeam,
            Members = members,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View(viewModel);
    }

    [HttpPost("Members/{userId}/SetRole")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetRole(string slug, Guid userId, SetMemberRoleModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return NotFound();
        }

        var canManage = await _teamService.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);
        if (!canManage)
        {
            return Forbid();
        }

        try
        {
            await _teamService.SetMemberRoleAsync(team.Id, userId, model.Role, user.Id);
            TempData["SuccessMessage"] = $"Member role updated to {model.Role}.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Members), new { slug });
    }

    [HttpPost("Members/{userId}/Remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(string slug, Guid userId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return NotFound();
        }

        var canManage = await _teamService.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);
        if (!canManage)
        {
            return Forbid();
        }

        try
        {
            await _teamService.RemoveMemberAsync(team.Id, userId, user.Id);
            TempData["SuccessMessage"] = "Member removed from team.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Members), new { slug });
    }

    [HttpGet("Resources")]
    public async Task<IActionResult> Resources(string slug)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return NotFound();
        }

        var canManage = await _teamResourceService.CanManageTeamResourcesAsync(team.Id, user.Id);
        if (!canManage)
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
                    _ => r.ResourceType.ToString()
                },
                Name = r.Name,
                Url = r.Url,
                GoogleId = r.GoogleId,
                ProvisionedAt = r.ProvisionedAt.ToDateTimeUtc(),
                LastSyncedAt = r.LastSyncedAt?.ToDateTimeUtc(),
                IsActive = r.IsActive,
                ErrorMessage = r.ErrorMessage
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpPost("Resources/LinkDrive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkDriveFolder(string slug, LinkDriveFolderModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return NotFound();
        }

        var canManage = await _teamResourceService.CanManageTeamResourcesAsync(team.Id, user.Id);
        if (!canManage)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Please enter a valid Google Drive folder URL.";
            return RedirectToAction(nameof(Resources), new { slug });
        }

        var result = await _teamResourceService.LinkDriveFolderAsync(team.Id, model.FolderUrl);

        if (result.Success)
        {
            TempData["SuccessMessage"] = $"Drive folder \"{result.Resource!.Name}\" linked successfully.";
        }
        else
        {
            var errorMessage = result.ErrorMessage ?? "Failed to link Drive folder.";
            if (result.ServiceAccountEmail != null)
            {
                errorMessage += $" Service account: {result.ServiceAccountEmail}";
            }
            TempData["ErrorMessage"] = errorMessage;
        }

        return RedirectToAction(nameof(Resources), new { slug });
    }

    [HttpPost("Resources/LinkGroup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkGroup(string slug, LinkGroupModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return NotFound();
        }

        var canManage = await _teamResourceService.CanManageTeamResourcesAsync(team.Id, user.Id);
        if (!canManage)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Please enter a valid Google Group email address.";
            return RedirectToAction(nameof(Resources), new { slug });
        }

        var result = await _teamResourceService.LinkGroupAsync(team.Id, model.GroupEmail);

        if (result.Success)
        {
            TempData["SuccessMessage"] = $"Google Group \"{result.Resource!.Name}\" linked successfully.";
        }
        else
        {
            var errorMessage = result.ErrorMessage ?? "Failed to link Google Group.";
            if (result.ServiceAccountEmail != null)
            {
                errorMessage += $" Service account: {result.ServiceAccountEmail}";
            }
            TempData["ErrorMessage"] = errorMessage;
        }

        return RedirectToAction(nameof(Resources), new { slug });
    }

    [HttpPost("Resources/{resourceId}/Unlink")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnlinkResource(string slug, Guid resourceId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return NotFound();
        }

        var canManage = await _teamResourceService.CanManageTeamResourcesAsync(team.Id, user.Id);
        if (!canManage)
        {
            return Forbid();
        }

        await _teamResourceService.UnlinkResourceAsync(resourceId);
        TempData["SuccessMessage"] = "Resource unlinked from team.";

        return RedirectToAction(nameof(Resources), new { slug });
    }

    [HttpPost("Resources/{resourceId}/Sync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncResource(string slug, Guid resourceId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return NotFound();
        }

        var canManage = await _teamResourceService.CanManageTeamResourcesAsync(team.Id, user.Id);
        if (!canManage)
        {
            return Forbid();
        }

        try
        {
            await _googleSyncService.SyncResourcePermissionsAsync(resourceId);
            TempData["SuccessMessage"] = "Resource permissions synced successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing resource {ResourceId}", resourceId);
            TempData["ErrorMessage"] = $"Failed to sync resource permissions: {ex.Message}";
        }

        return RedirectToAction(nameof(Resources), new { slug });
    }
}

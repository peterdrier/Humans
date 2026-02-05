using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
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
    private readonly UserManager<User> _userManager;
    private readonly ILogger<TeamAdminController> _logger;

    public TeamAdminController(
        ITeamService teamService,
        UserManager<User> userManager,
        ILogger<TeamAdminController> logger)
    {
        _teamService = teamService;
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
        catch (InvalidOperationException ex)
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
}

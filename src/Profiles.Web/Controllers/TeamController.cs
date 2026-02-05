using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Profiles.Application.Interfaces;
using Profiles.Domain.Entities;
using Profiles.Domain.Enums;
using Profiles.Web.Models;

namespace Profiles.Web.Controllers;

[Authorize]
[Route("Teams")]
public class TeamController : Controller
{
    private readonly ITeamService _teamService;
    private readonly UserManager<User> _userManager;
    private readonly ILogger<TeamController> _logger;

    public TeamController(
        ITeamService teamService,
        UserManager<User> userManager,
        ILogger<TeamController> logger)
    {
        _teamService = teamService;
        _userManager = userManager;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var teams = await _teamService.GetAllTeamsAsync();
        var userTeams = await _teamService.GetUserTeamsAsync(user.Id);
        var userTeamIds = userTeams.Select(ut => ut.TeamId).ToHashSet();
        var userMetaleadTeamIds = userTeams.Where(ut => ut.Role == TeamMemberRole.Metalead).Select(ut => ut.TeamId).ToHashSet();

        var isBoardMember = await _teamService.IsUserBoardMemberAsync(user.Id);

        var viewModel = new TeamIndexViewModel
        {
            Teams = teams.Select(t => new TeamSummaryViewModel
            {
                Id = t.Id,
                Name = t.Name,
                Description = t.Description,
                Slug = t.Slug,
                MemberCount = t.Members.Count(m => m.LeftAt == null),
                IsSystemTeam = t.IsSystemTeam,
                RequiresApproval = t.RequiresApproval,
                IsCurrentUserMember = userTeamIds.Contains(t.Id),
                IsCurrentUserMetalead = userMetaleadTeamIds.Contains(t.Id)
            }).ToList(),
            CanCreateTeam = isBoardMember
        };

        return View(viewModel);
    }

    [HttpGet("{slug}")]
    public async Task<IActionResult> Details(string slug)
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

        var isMember = await _teamService.IsUserMemberOfTeamAsync(team.Id, user.Id);
        var isMetalead = await _teamService.IsUserMetaleadOfTeamAsync(team.Id, user.Id);
        var isBoardMember = await _teamService.IsUserBoardMemberAsync(user.Id);
        var pendingRequest = await _teamService.GetUserPendingRequestAsync(team.Id, user.Id);
        var canManage = isMetalead || isBoardMember;

        var pendingRequestCount = 0;
        if (canManage)
        {
            var requests = await _teamService.GetPendingRequestsForTeamAsync(team.Id);
            pendingRequestCount = requests.Count;
        }

        var viewModel = new TeamDetailViewModel
        {
            Id = team.Id,
            Name = team.Name,
            Description = team.Description,
            Slug = team.Slug,
            IsActive = team.IsActive,
            RequiresApproval = team.RequiresApproval,
            IsSystemTeam = team.IsSystemTeam,
            SystemTeamType = team.SystemTeamType != SystemTeamType.None ? team.SystemTeamType.ToString() : null,
            CreatedAt = team.CreatedAt.ToDateTimeUtc(),
            Members = team.Members
                .Where(m => m.LeftAt == null)
                .OrderBy(m => m.Role)
                .ThenBy(m => m.JoinedAt)
                .Select(m => new TeamMemberViewModel
                {
                    UserId = m.UserId,
                    DisplayName = m.User?.DisplayName ?? "Unknown",
                    Email = m.User?.Email ?? "",
                    ProfilePictureUrl = m.User?.ProfilePictureUrl,
                    Role = m.Role.ToString(),
                    JoinedAt = m.JoinedAt.ToDateTimeUtc(),
                    IsMetalead = m.Role == TeamMemberRole.Metalead
                }).ToList(),
            IsCurrentUserMember = isMember,
            IsCurrentUserMetalead = isMetalead,
            CanCurrentUserJoin = !isMember && !team.IsSystemTeam && pendingRequest == null,
            CanCurrentUserLeave = isMember && !team.IsSystemTeam,
            CanCurrentUserManage = canManage,
            CurrentUserPendingRequestId = pendingRequest?.Id,
            PendingRequestCount = pendingRequestCount
        };

        return View(viewModel);
    }

    [HttpGet("My")]
    public async Task<IActionResult> MyTeams()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var memberships = await _teamService.GetUserTeamsAsync(user.Id);
        var isBoardMember = await _teamService.IsUserBoardMemberAsync(user.Id);

        // Get team IDs where user can manage and team is not a system team
        var manageableTeamIds = memberships
            .Where(m => (m.Role == TeamMemberRole.Metalead || isBoardMember) && !m.Team.IsSystemTeam)
            .Select(m => m.TeamId)
            .ToList();

        // Batch load pending request counts to avoid N+1
        var pendingCounts = manageableTeamIds.Count > 0
            ? await _teamService.GetPendingRequestCountsByTeamIdsAsync(manageableTeamIds)
            : new Dictionary<Guid, int>();

        var membershipVMs = memberships.Select(m => new MyTeamMembershipViewModel
        {
            TeamId = m.TeamId,
            TeamName = m.Team.Name,
            TeamSlug = m.Team.Slug,
            IsSystemTeam = m.Team.IsSystemTeam,
            Role = m.Role.ToString(),
            IsMetalead = m.Role == TeamMemberRole.Metalead,
            JoinedAt = m.JoinedAt.ToDateTimeUtc(),
            CanLeave = !m.Team.IsSystemTeam,
            PendingRequestCount = pendingCounts.GetValueOrDefault(m.TeamId, 0)
        }).ToList();

        // Get pending join requests for this user
        // Note: We'd need a method to get user's pending requests, for now just skip
        var viewModel = new MyTeamsViewModel
        {
            Memberships = membershipVMs,
            PendingRequests = []
        };

        return View(viewModel);
    }

    [HttpGet("{slug}/Join")]
    public async Task<IActionResult> Join(string slug)
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

        if (team.IsSystemTeam)
        {
            TempData["ErrorMessage"] = "Cannot join system teams directly.";
            return RedirectToAction(nameof(Details), new { slug });
        }

        var isMember = await _teamService.IsUserMemberOfTeamAsync(team.Id, user.Id);
        if (isMember)
        {
            TempData["ErrorMessage"] = "You are already a member of this team.";
            return RedirectToAction(nameof(Details), new { slug });
        }

        var pendingRequest = await _teamService.GetUserPendingRequestAsync(team.Id, user.Id);
        if (pendingRequest != null)
        {
            TempData["ErrorMessage"] = "You already have a pending request for this team.";
            return RedirectToAction(nameof(Details), new { slug });
        }

        var viewModel = new JoinTeamViewModel
        {
            TeamId = team.Id,
            TeamName = team.Name,
            RequiresApproval = team.RequiresApproval
        };

        return View(viewModel);
    }

    [HttpPost("{slug}/Join")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Join(string slug, JoinTeamViewModel model)
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

        if (team.Id != model.TeamId)
        {
            return BadRequest();
        }

        try
        {
            if (team.RequiresApproval)
            {
                await _teamService.RequestToJoinTeamAsync(team.Id, user.Id, model.Message);
                TempData["SuccessMessage"] = "Your request to join has been submitted and is pending approval.";
            }
            else
            {
                await _teamService.JoinTeamDirectlyAsync(team.Id, user.Id);
                TempData["SuccessMessage"] = "You have successfully joined the team!";
            }

            return RedirectToAction(nameof(Details), new { slug });
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
            return RedirectToAction(nameof(Details), new { slug });
        }
    }

    [HttpPost("{slug}/Leave")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Leave(string slug)
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

        try
        {
            await _teamService.LeaveTeamAsync(team.Id, user.Id);
            TempData["SuccessMessage"] = "You have left the team.";
            return RedirectToAction(nameof(Index));
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
            return RedirectToAction(nameof(Details), new { slug });
        }
    }

    [HttpPost("Requests/{id}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WithdrawRequest(Guid id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        try
        {
            await _teamService.WithdrawJoinRequestAsync(id, user.Id);
            TempData["SuccessMessage"] = "Your request has been withdrawn.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(MyTeams));
    }
}

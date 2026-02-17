using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Teams")]
public class TeamController : Controller
{
    private readonly ITeamService _teamService;
    private readonly UserManager<User> _userManager;
    private readonly HumansDbContext _dbContext;
    private readonly ILogger<TeamController> _logger;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IConfiguration _configuration;

    public TeamController(
        ITeamService teamService,
        UserManager<User> userManager,
        HumansDbContext dbContext,
        ILogger<TeamController> logger,
        IStringLocalizer<SharedResource> localizer,
        IConfiguration configuration)
    {
        _teamService = teamService;
        _userManager = userManager;
        _dbContext = dbContext;
        _logger = logger;
        _localizer = localizer;
        _configuration = configuration;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(int page = 1)
    {
        var pageSize = 12;
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var allTeams = await _teamService.GetAllTeamsAsync();
        var userTeams = await _teamService.GetUserTeamsAsync(user.Id);
        var userTeamIds = userTeams.Select(ut => ut.TeamId).ToHashSet();
        var userLeadTeamIds = userTeams.Where(ut => ut.Role == TeamMemberRole.Lead).Select(ut => ut.TeamId).ToHashSet();

        var isBoardMember = await _teamService.IsUserBoardMemberAsync(user.Id);

        var totalCount = allTeams.Count;
        var teams = allTeams
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TeamSummaryViewModel
            {
                Id = t.Id,
                Name = t.Name,
                Description = t.Description,
                Slug = t.Slug,
                MemberCount = t.Members.Count(m => m.LeftAt == null),
                IsSystemTeam = t.IsSystemTeam,
                RequiresApproval = t.RequiresApproval,
                IsCurrentUserMember = userTeamIds.Contains(t.Id),
                IsCurrentUserLead = userLeadTeamIds.Contains(t.Id)
            }).ToList();

        var viewModel = new TeamIndexViewModel
        {
            Teams = teams,
            CanCreateTeam = isBoardMember,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
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
        var isLead = await _teamService.IsUserLeadOfTeamAsync(team.Id, user.Id);
        var isBoardMember = await _teamService.IsUserBoardMemberAsync(user.Id);
        var isAdmin = await _teamService.IsUserAdminAsync(user.Id);
        var pendingRequest = await _teamService.GetUserPendingRequestAsync(team.Id, user.Id);
        var canManage = isLead || isBoardMember || isAdmin;

        var pendingRequestCount = 0;
        if (canManage)
        {
            var requests = await _teamService.GetPendingRequestsForTeamAsync(team.Id);
            pendingRequestCount = requests.Count;
        }

        // Get user IDs of active members to look up custom profile pictures
        var activeMembers = team.Members.Where(m => m.LeftAt == null).ToList();
        var memberUserIds = activeMembers.Select(m => m.UserId).ToList();

        // Load profiles that have custom pictures (only need Id and UserId, not the picture data)
        var profilesWithCustomPictures = await _dbContext.Profiles
            .AsNoTracking()
            .Where(p => memberUserIds.Contains(p.UserId) && p.ProfilePictureData != null)
            .Select(p => new { p.Id, p.UserId })
            .ToListAsync();

        var customPictureByUserId = profilesWithCustomPictures.ToDictionary(
            p => p.UserId,
            p => Url.Action("Picture", "Profile", new { id = p.Id })!);

        // Load active Google resources for this team
        var googleResources = await _dbContext.GoogleResources
            .AsNoTracking()
            .Where(gr => gr.TeamId == team.Id && gr.IsActive)
            .OrderBy(gr => gr.ResourceType)
            .ThenBy(gr => gr.Name)
            .ToListAsync();

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
            Resources = googleResources.Select(gr => new TeamResourceLinkViewModel
            {
                Name = gr.Name,
                Url = gr.Url,
                IconClass = gr.ResourceType switch
                {
                    GoogleResourceType.DriveFolder => "fa-solid fa-folder",
                    GoogleResourceType.DriveFile => "fa-solid fa-file",
                    GoogleResourceType.SharedDrive => "fa-solid fa-hard-drive",
                    GoogleResourceType.Group => "fa-solid fa-users",
                    _ => "fa-solid fa-link"
                }
            }).ToList(),
            Members = activeMembers
                .OrderBy(m => m.Role)
                .ThenBy(m => m.JoinedAt)
                .Select(m => new TeamMemberViewModel
                {
                    UserId = m.UserId,
                    DisplayName = m.User?.DisplayName ?? "Unknown",
                    Email = m.User?.Email ?? "",
                    ProfilePictureUrl = m.User?.ProfilePictureUrl,
                    HasCustomProfilePicture = customPictureByUserId.ContainsKey(m.UserId),
                    CustomProfilePictureUrl = customPictureByUserId.GetValueOrDefault(m.UserId),
                    Role = m.Role.ToString(),
                    JoinedAt = m.JoinedAt.ToDateTimeUtc(),
                    IsLead = m.Role == TeamMemberRole.Lead
                }).ToList(),
            IsCurrentUserMember = isMember,
            IsCurrentUserLead = isLead,
            CanCurrentUserJoin = !isMember && !team.IsSystemTeam && pendingRequest == null,
            CanCurrentUserLeave = isMember && !team.IsSystemTeam,
            CanCurrentUserManage = canManage,
            CurrentUserPendingRequestId = pendingRequest?.Id,
            PendingRequestCount = pendingRequestCount
        };

        return View(viewModel);
    }

    [HttpGet("Birthdays")]
    public async Task<IActionResult> Birthdays(int? month)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return NotFound();
        }

        var currentMonth = month ?? DateTime.UtcNow.Month;
        if (currentMonth < 1 || currentMonth > 12)
            currentMonth = DateTime.UtcNow.Month;

        // Load all active profiles that have a date of birth and are in at least one team
        var profilesWithBirthdays = await _dbContext.Profiles
            .AsNoTracking()
            .Include(p => p.User)
            .Where(p => p.DateOfBirth != null && !p.IsSuspended)
            .Where(p => p.DateOfBirth!.Value.Month == currentMonth)
            .OrderBy(p => p.DateOfBirth!.Value.Day)
            .Select(p => new
            {
                p.UserId,
                DisplayName = p.User!.DisplayName,
                ProfilePictureUrl = p.User.ProfilePictureUrl,
                HasCustomPicture = p.ProfilePictureData != null,
                ProfileId = p.Id,
                Day = p.DateOfBirth!.Value.Day,
                Month = p.DateOfBirth!.Value.Month
            })
            .ToListAsync();

        // Load team memberships for these users
        var userIds = profilesWithBirthdays.Select(p => p.UserId).ToList();
        var teamMemberships = await _dbContext.Set<TeamMember>()
            .AsNoTracking()
            .Include(tm => tm.Team)
            .Where(tm => userIds.Contains(tm.UserId) && tm.LeftAt == null && tm.Team.SystemTeamType == SystemTeamType.None)
            .Select(tm => new { tm.UserId, tm.Team.Name })
            .ToListAsync();

        var teamsByUser = teamMemberships
            .GroupBy(tm => tm.UserId)
            .ToDictionary(g => g.Key, g => g.Select(tm => tm.Name).Distinct(StringComparer.Ordinal).ToList());

        var monthName = new DateTime(2000, currentMonth, 1).ToString("MMMM", CultureInfo.CurrentCulture);

        var viewModel = new BirthdayCalendarViewModel
        {
            CurrentMonth = currentMonth,
            CurrentMonthName = monthName,
            Birthdays = profilesWithBirthdays.Select(p => new BirthdayEntryViewModel
            {
                UserId = p.UserId,
                DisplayName = p.DisplayName,
                EffectiveProfilePictureUrl = p.HasCustomPicture
                    ? Url.Action("Picture", "Profile", new { id = p.ProfileId })
                    : p.ProfilePictureUrl,
                DayOfMonth = p.Day,
                Month = p.Month,
                MonthName = monthName,
                TeamNames = teamsByUser.GetValueOrDefault(p.UserId, [])
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpGet("Map")]
    public async Task<IActionResult> Map()
    {
        var markers = await _dbContext.Profiles
            .AsNoTracking()
            .Include(p => p.User)
            .Where(p => p.Latitude != null && p.Longitude != null && !p.IsSuspended)
            .Select(p => new MapMarkerViewModel
            {
                DisplayName = p.User!.DisplayName,
                Latitude = p.Latitude!.Value,
                Longitude = p.Longitude!.Value,
                City = p.City,
                CountryCode = p.CountryCode
            })
            .ToListAsync();

        ViewData["GoogleMapsApiKey"] = _configuration["GoogleMaps:ApiKey"];

        return View(new MapViewModel { Markers = markers });
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
            .Where(m => (m.Role == TeamMemberRole.Lead || isBoardMember) && !m.Team.IsSystemTeam)
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
            IsLead = m.Role == TeamMemberRole.Lead,
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
            TempData["ErrorMessage"] = _localizer["Team_CannotJoinSystem"].Value;
            return RedirectToAction(nameof(Details), new { slug });
        }

        var isMember = await _teamService.IsUserMemberOfTeamAsync(team.Id, user.Id);
        if (isMember)
        {
            TempData["ErrorMessage"] = _localizer["Team_AlreadyMember"].Value;
            return RedirectToAction(nameof(Details), new { slug });
        }

        var pendingRequest = await _teamService.GetUserPendingRequestAsync(team.Id, user.Id);
        if (pendingRequest != null)
        {
            TempData["ErrorMessage"] = _localizer["Team_AlreadyPendingRequest"].Value;
            return RedirectToAction(nameof(Details), new { slug });
        }

        var viewModel = new JoinTeamViewModel
        {
            TeamId = team.Id,
            TeamName = team.Name,
            TeamSlug = team.Slug,
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
                TempData["SuccessMessage"] = _localizer["Team_JoinRequestSubmitted"].Value;
            }
            else
            {
                await _teamService.JoinTeamDirectlyAsync(team.Id, user.Id);
                TempData["SuccessMessage"] = _localizer["Team_Joined"].Value;
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
            TempData["SuccessMessage"] = _localizer["Team_Left"].Value;
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
            TempData["SuccessMessage"] = _localizer["Team_RequestWithdrawn"].Value;
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(MyTeams));
    }
}

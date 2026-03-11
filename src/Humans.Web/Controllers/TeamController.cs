using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Teams")]
public class TeamController : Controller
{
    private readonly ITeamService _teamService;
    private readonly UserManager<User> _userManager;
    private readonly IProfileService _profileService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TeamController> _logger;

    public TeamController(
        ITeamService teamService,
        UserManager<User> userManager,
        IProfileService profileService,
        ITeamResourceService teamResourceService,
        IGoogleSyncService googleSyncService,
        IStringLocalizer<SharedResource> localizer,
        IConfiguration configuration,
        ILogger<TeamController> logger)
    {
        _teamService = teamService;
        _userManager = userManager;
        _profileService = profileService;
        _teamResourceService = teamResourceService;
        _googleSyncService = googleSyncService;
        _localizer = localizer;
        _configuration = configuration;
        _logger = logger;
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

        TeamSummaryViewModel ToSummary(Team t) => new()
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
        };

        var myTeams = allTeams
            .Where(t => userTeamIds.Contains(t.Id))
            .Select(ToSummary)
            .ToList();

        var otherTeams = allTeams
            .Where(t => !userTeamIds.Contains(t.Id))
            .ToList();

        var totalCount = otherTeams.Count;
        var teams = otherTeams
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ToSummary)
            .ToList();

        ViewBag.CanViewSync = User.IsInRole("TeamsAdmin") || User.IsInRole("Board") || User.IsInRole("Admin");

        var viewModel = new TeamIndexViewModel
        {
            MyTeams = myTeams,
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
        var isTeamsAdmin = User.IsInRole("TeamsAdmin");
        var canManage = isLead || isBoardMember || isAdmin || isTeamsAdmin;

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
        var profilesWithCustomPictures = await _profileService.GetCustomPictureInfoByUserIdsAsync(memberUserIds);

        var customPictureByUserId = profilesWithCustomPictures.ToDictionary(
            p => p.UserId,
            p => Url.Action("Picture", "Profile", new { id = p.ProfileId, v = p.UpdatedAtTicks })!);

        // Load active Google resources for this team
        var googleResources = await _teamResourceService.GetTeamResourcesAsync(team.Id);

        // Load role definitions for roster section
        var roleDefinitions = await _teamService.GetRoleDefinitionsAsync(team.Id);

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
            RoleDefinitions = roleDefinitions.Select(TeamRoleDefinitionViewModel.FromEntity).ToList(),
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
                .ThenBy(m => m.User.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(m => new TeamMemberViewModel
                {
                    UserId = m.UserId,
                    DisplayName = m.User.DisplayName,
                    Email = m.User.Email ?? "",
                    ProfilePictureUrl = m.User.ProfilePictureUrl,
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
            CanCurrentUserEditTeam = isBoardMember || isAdmin,
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

        // Load all active profiles that have a date of birth
        var profilesWithBirthdays = await _profileService.GetBirthdayProfilesAsync(currentMonth);

        // Load team memberships for these users
        var userIds = profilesWithBirthdays.Select(p => p.UserId).ToList();
        var teamsByUser = await _teamService.GetNonSystemTeamNamesByUserIdsAsync(userIds);

        var monthName = new DateTime(2000, currentMonth, 1).ToString("MMMM", CultureInfo.CurrentCulture);

        var effectiveUrls = await Helpers.ProfilePictureUrlHelper.BuildEffectiveUrlsAsync(
            _profileService, Url,
            profilesWithBirthdays.Select(p => (p.UserId, p.ProfilePictureUrl)));

        var viewModel = new BirthdayCalendarViewModel
        {
            CurrentMonth = currentMonth,
            CurrentMonthName = monthName,
            Birthdays = profilesWithBirthdays.Select(p => new BirthdayEntryViewModel
            {
                UserId = p.UserId,
                DisplayName = p.DisplayName,
                EffectiveProfilePictureUrl = effectiveUrls.GetValueOrDefault(p.UserId),
                DayOfMonth = p.Day,
                Month = p.Month,
                MonthName = monthName,
                TeamNames = teamsByUser.GetValueOrDefault(p.UserId, [])
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpGet("Roster")]
    public async Task<IActionResult> Roster(string? priority, string? status)
    {
        var definitions = await _teamService.GetAllRoleDefinitionsAsync();

        var slots = new List<RosterSlotViewModel>();
        foreach (var def in definitions)
        {
            for (var i = 0; i < def.SlotCount; i++)
            {
                var assignment = def.Assignments.FirstOrDefault(a => a.SlotIndex == i);
                var slotPriority = i < def.Priorities.Count ? def.Priorities[i] : SlotPriority.None;
                var priorityStr = slotPriority.ToString();

                slots.Add(new RosterSlotViewModel
                {
                    TeamName = def.Team.Name,
                    TeamSlug = def.Team.Slug,
                    RoleName = def.Name,
                    SlotNumber = i + 1,
                    Priority = priorityStr,
                    PriorityBadgeClass = slotPriority switch
                    {
                        SlotPriority.Critical => "bg-danger",
                        SlotPriority.Important => "bg-warning text-dark",
                        SlotPriority.NiceToHave => "bg-secondary",
                        _ => "bg-light text-dark"
                    },
                    IsFilled = assignment != null,
                    AssignedUserName = assignment?.TeamMember?.User?.DisplayName
                });
            }
        }

        // Apply filters
        if (!string.IsNullOrEmpty(priority))
            slots = slots.Where(s => string.Equals(s.Priority, priority, StringComparison.OrdinalIgnoreCase)).ToList();

        if (string.Equals(status, "Open", StringComparison.OrdinalIgnoreCase))
            slots = slots.Where(s => !s.IsFilled).ToList();
        else if (string.Equals(status, "Filled", StringComparison.OrdinalIgnoreCase))
            slots = slots.Where(s => s.IsFilled).ToList();

        // Sort: Critical first, then by team name
        slots = slots
            .OrderBy(s => s.Priority switch { "Critical" => 0, "Important" => 1, "NiceToHave" => 2, _ => 3 })
            .ThenBy(s => s.TeamName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.RoleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.SlotNumber)
            .ToList();

        return View(new RosterSummaryViewModel { Slots = slots, PriorityFilter = priority, StatusFilter = status });
    }

    [HttpGet("Map")]
    public async Task<IActionResult> Map()
    {
        var profiles = await _profileService.GetApprovedProfilesWithLocationAsync();

        var effectiveUrls = await Helpers.ProfilePictureUrlHelper.BuildEffectiveUrlsAsync(
            _profileService, Url,
            profiles.Select(p => (p.UserId, p.ProfilePictureUrl)));

        var markers = profiles.Select(p => new MapMarkerViewModel
        {
            UserId = p.UserId,
            DisplayName = p.DisplayName,
            ProfilePictureUrl = effectiveUrls.GetValueOrDefault(p.UserId),
            Latitude = p.Latitude,
            Longitude = p.Longitude,
            City = p.City,
            CountryCode = p.CountryCode
        }).ToList();

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

    [HttpGet("Sync")]
    [Authorize(Roles = "TeamsAdmin,Board,Admin")]
    public IActionResult Sync()
    {
        var viewModel = new TeamSyncViewModel
        {
            CanExecuteActions = User.IsInRole("Admin")
        };
        return View(viewModel);
    }

    [HttpGet("Sync/Preview/{resourceType}")]
    [Authorize(Roles = "TeamsAdmin,Board,Admin")]
    public async Task<IActionResult> SyncPreview(GoogleResourceType resourceType)
    {
        var result = await _googleSyncService.SyncResourcesByTypeAsync(resourceType, SyncAction.Preview);
        return Json(result);
    }

    [HttpPost("Sync/Execute/{resourceId}")]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncExecute(Guid resourceId, [FromQuery] SyncAction action)
    {
        var result = await _googleSyncService.SyncSingleResourceAsync(resourceId, action);
        return Json(result);
    }

    [HttpPost("Sync/ExecuteAll/{resourceType}")]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncExecuteAll(GoogleResourceType resourceType, [FromQuery] SyncAction action)
    {
        var result = await _googleSyncService.SyncResourcesByTypeAsync(resourceType, action);
        return Json(result);
    }

    [HttpGet("Summary")]
    [Authorize(Roles = "Board,Admin,TeamsAdmin")]
    public async Task<IActionResult> Summary(int page = 1)
    {
        var pageSize = 20;
        var (teams, totalCount) = await _teamService.GetAllTeamsForAdminAsync(page, pageSize);

        var viewModel = new AdminTeamListViewModel
        {
            Teams = teams.Select(t => new AdminTeamViewModel
            {
                Id = t.Id,
                Name = t.Name,
                Slug = t.Slug,
                IsActive = t.IsActive,
                RequiresApproval = t.RequiresApproval,
                IsSystemTeam = t.IsSystemTeam,
                SystemTeamType = t.SystemTeamType != SystemTeamType.None ? t.SystemTeamType.ToString() : null,
                MemberCount = t.Members.Count,
                PendingRequestCount = t.JoinRequests.Count,
                HasMailGroup = t.GoogleResources.Any(r => r.ResourceType == GoogleResourceType.Group && r.IsActive),
                DriveResourceCount = t.GoogleResources.Count(r => r.ResourceType != GoogleResourceType.Group && r.IsActive),
                CreatedAt = t.CreatedAt.ToDateTimeUtc()
            }).ToList(),
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View(viewModel);
    }

    [HttpGet("Create")]
    [Authorize(Roles = "Board,Admin")]
    public IActionResult CreateTeam()
    {
        return View(new CreateTeamViewModel());
    }

    [HttpPost("Create")]
    [Authorize(Roles = "Board,Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTeam(CreateTeamViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            var team = await _teamService.CreateTeamAsync(model.Name, model.Description, model.RequiresApproval, model.GoogleGroupPrefix);
            var currentUser = await _userManager.GetUserAsync(User);
            _logger.LogInformation("Admin {AdminId} created team {TeamId} ({TeamName})", currentUser?.Id, team.Id, team.Name);

            if (!string.IsNullOrEmpty(model.GoogleGroupPrefix))
            {
                try
                {
                    await _googleSyncService.EnsureTeamGroupAsync(team.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create Google Group for new team {TeamId}, clearing prefix", team.Id);
                    await _teamService.UpdateTeamAsync(team.Id, team.Name, team.Description, team.RequiresApproval, team.IsActive, null);
                    TempData["SuccessMessage"] = string.Format(_localizer["Admin_TeamCreated"].Value, team.Name);
                    TempData["ErrorMessage"] = $"Team created but Google Group setup failed: {ex.Message}. The group prefix has been cleared.";
                    return RedirectToAction(nameof(Summary));
                }
            }

            TempData["SuccessMessage"] = string.Format(_localizer["Admin_TeamCreated"].Value, team.Name);
            return RedirectToAction(nameof(Summary));
        }
        catch (DbUpdateException)
        {
            ModelState.AddModelError("GoogleGroupPrefix", "This Google Group prefix is already in use by another team.");
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating team");
            ModelState.AddModelError("", _localizer["Admin_TeamCreateError"].Value);
            return View(model);
        }
    }

    [HttpGet("{id:guid}/Edit")]
    [Authorize(Roles = "Board,Admin")]
    public async Task<IActionResult> EditTeam(Guid id)
    {
        var team = await _teamService.GetTeamByIdAsync(id);
        if (team == null)
        {
            return NotFound();
        }

        var viewModel = new EditTeamViewModel
        {
            Id = team.Id,
            Name = team.Name,
            Description = team.Description,
            GoogleGroupPrefix = team.GoogleGroupPrefix,
            GoogleGroupEmail = team.GoogleGroupEmail,
            RequiresApproval = team.RequiresApproval,
            IsActive = team.IsActive,
            IsSystemTeam = team.IsSystemTeam
        };

        return View(viewModel);
    }

    [HttpPost("{id:guid}/Edit")]
    [Authorize(Roles = "Board,Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditTeam(Guid id, EditTeamViewModel model)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            await _teamService.UpdateTeamAsync(id, model.Name, model.Description, model.RequiresApproval, model.IsActive, model.GoogleGroupPrefix);
            var currentUser = await _userManager.GetUserAsync(User);
            _logger.LogInformation("Admin {AdminId} updated team {TeamId}", currentUser?.Id, id);

            // Handles prefix set, changed, or cleared (deactivates old resource if needed)
            try
            {
                await _googleSyncService.EnsureTeamGroupAsync(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync Google Group for team {TeamId}", id);
                TempData["SuccessMessage"] = _localizer["Admin_TeamUpdated"].Value;
                TempData["ErrorMessage"] = $"Team updated but Google Group setup failed: {ex.Message}";
                return RedirectToAction(nameof(Summary));
            }

            TempData["SuccessMessage"] = _localizer["Admin_TeamUpdated"].Value;
            return RedirectToAction(nameof(Summary));
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View(model);
        }
        catch (DbUpdateException)
        {
            ModelState.AddModelError("GoogleGroupPrefix", "This Google Group prefix is already in use by another team.");
            return View(model);
        }
    }

    [HttpPost("{id:guid}/Delete")]
    [Authorize(Roles = "Board,Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTeam(Guid id)
    {
        try
        {
            await _teamService.DeleteTeamAsync(id);
            var currentUser = await _userManager.GetUserAsync(User);
            _logger.LogInformation("Admin {AdminId} deactivated team {TeamId}", currentUser?.Id, id);

            TempData["SuccessMessage"] = _localizer["Admin_TeamDeactivated"].Value;
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Summary));
    }

}

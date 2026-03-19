using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.AspNetCore.Identity;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Teams")]
public class TeamController : HumansControllerBase
{
    private readonly ITeamService _teamService;
    private readonly IProfileService _profileService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly ISystemTeamSync _systemTeamSync;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TeamController> _logger;

    public TeamController(
        ITeamService teamService,
        UserManager<User> userManager,
        IProfileService profileService,
        ITeamResourceService teamResourceService,
        IGoogleSyncService googleSyncService,
        ISystemTeamSync systemTeamSync,
        IShiftManagementService shiftMgmt,
        IStringLocalizer<SharedResource> localizer,
        IConfiguration configuration,
        ILogger<TeamController> logger)
        : base(userManager)
    {
        _teamService = teamService;
        _profileService = profileService;
        _teamResourceService = teamResourceService;
        _googleSyncService = googleSyncService;
        _systemTeamSync = systemTeamSync;
        _shiftMgmt = shiftMgmt;
        _localizer = localizer;
        _configuration = configuration;
        _logger = logger;
    }

    [AllowAnonymous]
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserAsync();
        var isAuthenticated = user is not null;

        if (!isAuthenticated)
        {
            // Anonymous: show only public teams
            var allTeams = await _teamService.GetAllTeamsAsync();
            var teamById = allTeams.ToDictionary(t => t.Id);

            var publicTeams = allTeams
                .Where(t => t.IsPublicPage && !t.IsSystemTeam && t.ParentTeamId == null)
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => new TeamSummaryViewModel
                {
                    Id = t.Id,
                    Name = t.Name,
                    Description = t.Description,
                    Slug = t.Slug,
                    MemberCount = t.Members.Count(m => m.LeftAt == null),
                    IsPublicPage = true
                })
                .ToList();

            return View(new TeamIndexViewModel
            {
                Departments = publicTeams,
                IsAuthenticated = false
            });
        }

        var allTeamsAuth = await _teamService.GetAllTeamsAsync();
        var userTeams = await _teamService.GetUserTeamsAsync(user!.Id);
        var userTeamIds = userTeams.Select(ut => ut.TeamId).ToHashSet();
        var userCoordinatorTeamIds = userTeams.Where(ut => ut.Role == TeamMemberRole.Coordinator).Select(ut => ut.TeamId).ToHashSet();

        var isBoardMember = await _teamService.IsUserBoardMemberAsync(user.Id);

        // Build parent lookup for sub-team display
        var teamById2 = allTeamsAuth.ToDictionary(t => t.Id);

        TeamSummaryViewModel ToSummary(Team t)
        {
            Team? parent = t.ParentTeamId.HasValue && teamById2.TryGetValue(t.ParentTeamId.Value, out var p) ? p : null;
            return new()
            {
                Id = t.Id,
                Name = t.Name,
                Description = t.Description,
                Slug = t.Slug,
                MemberCount = t.Members.Count(m => m.LeftAt == null),
                IsSystemTeam = t.IsSystemTeam,
                RequiresApproval = t.RequiresApproval,
                IsPublicPage = t.IsPublicPage,
                IsCurrentUserMember = userTeamIds.Contains(t.Id),
                IsCurrentUserCoordinator = userCoordinatorTeamIds.Contains(t.Id),
                ParentTeamName = parent?.Name,
                ParentTeamSlug = parent?.Slug
            };
        }

        var allSummaries = allTeamsAuth.Select(ToSummary).ToList();

        var myTeams = allSummaries
            .Where(t => userTeamIds.Contains(t.Id))
            .OrderBy(t => t.SortKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var departments = allSummaries
            .Where(t => !userTeamIds.Contains(t.Id) && !t.IsSystemTeam)
            .OrderBy(t => t.SortKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var systemTeams = allSummaries
            .Where(t => !userTeamIds.Contains(t.Id) && t.IsSystemTeam)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ViewBag.CanViewSync = RoleChecks.IsTeamsAdminBoardOrAdmin(User);

        var viewModel = new TeamIndexViewModel
        {
            MyTeams = myTeams,
            Departments = departments,
            SystemTeams = systemTeams,
            CanCreateTeam = isBoardMember || RoleChecks.IsTeamsAdminBoardOrAdmin(User),
            IsAuthenticated = true
        };

        return View(viewModel);
    }

    [AllowAnonymous]
    [HttpGet("{slug}")]
    public async Task<IActionResult> Details(string slug)
    {
        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return NotFound();
        }

        var user = await GetCurrentUserAsync();
        var isAuthenticated = user is not null;

        // Anonymous visitors can only see public teams
        if (!isAuthenticated && !team.IsPublicPage)
        {
            return NotFound();
        }

        // Build page content HTML
        string? pageContentHtml = null;
        if (!string.IsNullOrEmpty(team.PageContent))
        {
            var sanitizer = new Ganss.Xss.HtmlSanitizer();
            pageContentHtml = sanitizer.Sanitize(Markdig.Markdown.ToHtml(team.PageContent));
        }

        // Look up who last edited the page
        string? pageContentUpdatedByDisplayName = null;
        if (team.PageContentUpdatedByUserId.HasValue)
        {
            var editor = await FindUserByIdAsync(team.PageContentUpdatedByUserId.Value);
            pageContentUpdatedByDisplayName = editor?.DisplayName;
        }

        if (!isAuthenticated)
        {
            // Anonymous: show public page with coordinators only
            var activeMembers = team.Members.Where(m => m.LeftAt == null).ToList();
            var coordinators = activeMembers.Where(m => m.Role == TeamMemberRole.Coordinator).ToList();
            var coordinatorUserIds = coordinators.Select(m => m.UserId).ToList();
            var profilesWithCustomPictures = await _profileService.GetCustomPictureInfoByUserIdsAsync(coordinatorUserIds);
            var customPictureByUserId = profilesWithCustomPictures.ToDictionary(
                p => p.UserId,
                p => Url.Action(nameof(ProfileController.Picture), "Profile", new { id = p.ProfileId, v = p.UpdatedAtTicks })!);

            var viewModel = new TeamDetailViewModel
            {
                Id = team.Id,
                Name = team.DisplayName,
                Description = team.Description,
                Slug = team.Slug,
                IsActive = team.IsActive,
                IsSystemTeam = team.IsSystemTeam,
                CreatedAt = team.CreatedAt.ToDateTimeUtc(),
                IsPublicPage = team.IsPublicPage,
                PageContent = team.PageContent,
                PageContentHtml = pageContentHtml,
                CallsToAction = team.CallsToAction ?? [],
                PageContentUpdatedAt = team.PageContentUpdatedAt?.ToDateTimeUtc(),
                PageContentUpdatedByDisplayName = pageContentUpdatedByDisplayName,
                IsAuthenticated = false,
                Members = coordinators
                    .OrderBy(m => m.User.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new TeamMemberViewModel
                    {
                        UserId = m.UserId,
                        DisplayName = m.User.DisplayName,
                        ProfilePictureUrl = m.User.ProfilePictureUrl,
                        HasCustomProfilePicture = customPictureByUserId.ContainsKey(m.UserId),
                        CustomProfilePictureUrl = customPictureByUserId.GetValueOrDefault(m.UserId),
                        Role = m.Role.ToString(),
                        IsCoordinator = true
                    }).ToList(),
                ParentTeam = team.ParentTeam,
                ChildTeams = team.ChildTeams.Where(c => c.IsActive && c.IsPublicPage).OrderBy(c => c.Name, StringComparer.Ordinal).ToList()
            };

            return View(viewModel);
        }

        // Authenticated path — full details
        var isMember = await _teamService.IsUserMemberOfTeamAsync(team.Id, user!.Id);
        var isCoordinator = await _teamService.IsUserCoordinatorOfTeamAsync(team.Id, user.Id);
        var isBoardMember = RoleChecks.IsBoard(User);
        var isAdmin = RoleChecks.IsAdmin(User);
        var pendingRequest = await _teamService.GetUserPendingRequestAsync(team.Id, user.Id);
        var isTeamsAdmin = RoleChecks.IsTeamsAdmin(User);
        var canManage = isCoordinator || isBoardMember || isAdmin || isTeamsAdmin;

        var pendingRequestCount = 0;
        if (canManage)
        {
            var requests = await _teamService.GetPendingRequestsForTeamAsync(team.Id);
            pendingRequestCount = requests.Count;
        }

        // Get user IDs of active members to look up custom profile pictures
        var allActiveMembers = team.Members.Where(m => m.LeftAt == null).ToList();
        var memberUserIds = allActiveMembers.Select(m => m.UserId).ToList();

        // Load profiles that have custom pictures (only need Id and UserId, not the picture data)
        var allProfilesWithCustomPictures = await _profileService.GetCustomPictureInfoByUserIdsAsync(memberUserIds);

        var allCustomPictureByUserId = allProfilesWithCustomPictures.ToDictionary(
            p => p.UserId,
            p => Url.Action(nameof(ProfileController.Picture), "Profile", new { id = p.ProfileId, v = p.UpdatedAtTicks })!);

        // Load active Google resources for this team
        var googleResources = await _teamResourceService.GetTeamResourcesAsync(team.Id);

        // Load role definitions for roster section
        var roleDefinitions = await _teamService.GetRoleDefinitionsAsync(team.Id);

        // Load shifts summary for departments (parent teams that aren't system teams)
        ShiftsSummaryCardViewModel? shiftsSummary = null;
        if (team.ParentTeamId == null && team.SystemTeamType == SystemTeamType.None)
        {
            var es = await _shiftMgmt.GetActiveAsync();
            if (es != null)
            {
                var summaryData = await _shiftMgmt.GetShiftsSummaryAsync(es.Id, team.Id);
                if (summaryData != null)
                {
                    var canManageShifts = RoleChecks.IsAdmin(User) ||
                        User.IsInRole(RoleNames.VolunteerCoordinator) ||
                        await _shiftMgmt.IsDeptCoordinatorAsync(user.Id, team.Id);
                    shiftsSummary = new ShiftsSummaryCardViewModel
                    {
                        TotalSlots = summaryData.TotalSlots,
                        ConfirmedCount = summaryData.ConfirmedCount,
                        PendingCount = summaryData.PendingCount,
                        UniqueVolunteerCount = summaryData.UniqueVolunteerCount,
                        ShiftsUrl = Url.Action("Index", "ShiftAdmin", new { slug })!,
                        CanManageShifts = canManageShifts
                    };
                }
            }
        }

        var authViewModel = new TeamDetailViewModel
        {
            Id = team.Id,
            Name = team.DisplayName,
            Description = team.Description,
            Slug = team.Slug,
            IsActive = team.IsActive,
            RequiresApproval = team.RequiresApproval,
            IsSystemTeam = team.IsSystemTeam,
            SystemTeamType = team.SystemTeamType != SystemTeamType.None ? team.SystemTeamType.ToString() : null,
            CreatedAt = team.CreatedAt.ToDateTimeUtc(),
            IsPublicPage = team.IsPublicPage,
            PageContent = team.PageContent,
            PageContentHtml = pageContentHtml,
            CallsToAction = team.CallsToAction ?? [],
            PageContentUpdatedAt = team.PageContentUpdatedAt?.ToDateTimeUtc(),
            PageContentUpdatedByDisplayName = pageContentUpdatedByDisplayName,
            IsAuthenticated = true,
            CanEditPageContent = canManage,
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
            Members = allActiveMembers
                .OrderBy(m => m.Role)
                .ThenBy(m => m.User.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(m => new TeamMemberViewModel
                {
                    UserId = m.UserId,
                    DisplayName = m.User.DisplayName,
                    Email = m.User.Email ?? "",
                    ProfilePictureUrl = m.User.ProfilePictureUrl,
                    HasCustomProfilePicture = allCustomPictureByUserId.ContainsKey(m.UserId),
                    CustomProfilePictureUrl = allCustomPictureByUserId.GetValueOrDefault(m.UserId),
                    Role = m.Role.ToString(),
                    JoinedAt = m.JoinedAt.ToDateTimeUtc(),
                    IsCoordinator = m.Role == TeamMemberRole.Coordinator
                }).ToList(),
            ParentTeam = team.ParentTeam,
            ChildTeams = team.ChildTeams.Where(c => c.IsActive).OrderBy(c => c.Name, StringComparer.Ordinal).ToList(),
            IsCurrentUserMember = isMember,
            IsCurrentUserCoordinator = isCoordinator,
            CanCurrentUserJoin = !isMember && !team.IsSystemTeam && pendingRequest == null,
            CanCurrentUserLeave = isMember && !team.IsSystemTeam,
            CanCurrentUserManage = canManage,
            CanCurrentUserEditTeam = isBoardMember || isAdmin || isTeamsAdmin,
            CurrentUserPendingRequestId = pendingRequest?.Id,
            PendingRequestCount = pendingRequestCount,
            ShiftsSummary = shiftsSummary
        };

        return View(authViewModel);
    }

    [HttpGet("Birthdays")]
    public async Task<IActionResult> Birthdays(int? month)
    {
        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
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

    [HttpGet("Search")]
    public async Task<IActionResult> Search(string? q)
    {
        var viewModel = new HumanSearchViewModel { Query = q };

        if (!q.HasSearchTerm())
        {
            return View(viewModel);
        }

        var results = await _profileService.SearchHumansAsync(q);

        viewModel.Results = results
            .Select(r => r.ToHumanSearchViewModel(Url))
            .ToList();

        return View(viewModel);
    }

    [HttpGet("Roster")]
    public async Task<IActionResult> Roster(string? priority, string? status, string? period)
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
                    RoleDescription = def.Description,
                    RoleDefinitionId = def.Id,
                    SlotNumber = i + 1,
                    Priority = priorityStr,
                    PriorityBadgeClass = slotPriority switch
                    {
                        SlotPriority.Critical => "bg-danger",
                        SlotPriority.Important => "bg-warning text-dark",
                        SlotPriority.NiceToHave => "bg-secondary",
                        _ => "bg-light text-dark"
                    },
                    Period = def.Period.ToString(),
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

        if (!string.IsNullOrEmpty(period))
            slots = slots.Where(s => string.Equals(s.Period, period, StringComparison.OrdinalIgnoreCase)).ToList();

        // Sort: Critical first, then by team name
        slots = slots
            .OrderBy(s => s.Priority switch { "Critical" => 0, "Important" => 1, "NiceToHave" => 2, _ => 3 })
            .ThenBy(s => s.TeamName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.RoleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.SlotNumber)
            .ToList();

        return View(new RosterSummaryViewModel { Slots = slots, PriorityFilter = priority, StatusFilter = status, PeriodFilter = period });
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
        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var memberships = await _teamService.GetUserTeamsAsync(user.Id);
        var isBoardMember = await _teamService.IsUserBoardMemberAsync(user.Id);

        // Get team IDs where user can manage and team is not a system team
        var manageableTeamIds = memberships
            .Where(m => (m.Role == TeamMemberRole.Coordinator || isBoardMember) && !m.Team.IsSystemTeam)
            .Select(m => m.TeamId)
            .ToList();

        // Batch load pending request counts to avoid N+1
        var pendingCounts = manageableTeamIds.Count > 0
            ? await _teamService.GetPendingRequestCountsByTeamIdsAsync(manageableTeamIds)
            : new Dictionary<Guid, int>();

        var membershipVMs = memberships.Select(m => new MyTeamMembershipViewModel
        {
            TeamId = m.TeamId,
            TeamName = m.Team.DisplayName,
            TeamSlug = m.Team.Slug,
            IsSystemTeam = m.Team.IsSystemTeam,
            Role = m.Role.ToString(),
            IsCoordinator = m.Role == TeamMemberRole.Coordinator,
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
        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return NotFound();
        }

        if (team.IsSystemTeam)
        {
            SetError(_localizer["Team_CannotJoinSystem"].Value);
            return RedirectToAction(nameof(Details), new { slug });
        }

        var isMember = await _teamService.IsUserMemberOfTeamAsync(team.Id, user.Id);
        if (isMember)
        {
            SetError(_localizer["Team_AlreadyMember"].Value);
            return RedirectToAction(nameof(Details), new { slug });
        }

        var pendingRequest = await _teamService.GetUserPendingRequestAsync(team.Id, user.Id);
        if (pendingRequest != null)
        {
            SetError(_localizer["Team_AlreadyPendingRequest"].Value);
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
        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
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
                SetSuccess(_localizer["Team_JoinRequestSubmitted"].Value);
            }
            else
            {
                await _teamService.JoinTeamDirectlyAsync(team.Id, user.Id);
                SetSuccess(_localizer["Team_Joined"].Value);
            }

            return RedirectToAction(nameof(Details), new { slug });
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
            return RedirectToAction(nameof(Details), new { slug });
        }
    }

    [HttpPost("{slug}/Leave")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Leave(string slug)
    {
        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null)
        {
            return NotFound();
        }

        try
        {
            var wasCoordinator = await _teamService.LeaveTeamAsync(team.Id, user.Id);
            if (wasCoordinator)
            {
                await _systemTeamSync.SyncCoordinatorsMembershipForUserAsync(user.Id);
            }
            SetSuccess(_localizer["Team_Left"].Value);
            return RedirectToAction(nameof(Index));
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
            return RedirectToAction(nameof(Details), new { slug });
        }
    }

    [HttpPost("Requests/{id}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WithdrawRequest(Guid id)
    {
        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        try
        {
            await _teamService.WithdrawJoinRequestAsync(id, user.Id);
            SetSuccess(_localizer["Team_RequestWithdrawn"].Value);
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(MyTeams));
    }

    [HttpGet("Sync")]
    [Authorize(Roles = RoleGroups.TeamsAdminBoardOrAdmin)]
    public IActionResult Sync()
    {
        var viewModel = new TeamSyncViewModel
        {
            CanExecuteActions = RoleChecks.IsAdmin(User)
        };
        return View(viewModel);
    }

    [HttpGet("Sync/Preview/{resourceType}")]
    [Authorize(Roles = RoleGroups.TeamsAdminBoardOrAdmin)]
    public async Task<IActionResult> SyncPreview(GoogleResourceType resourceType)
    {
        var result = await _googleSyncService.SyncResourcesByTypeAsync(resourceType, SyncAction.Preview);
        return Json(result);
    }

    [HttpPost("Sync/Execute/{resourceId}")]
    [Authorize(Roles = RoleNames.Admin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncExecute(Guid resourceId)
    {
        var result = await _googleSyncService.SyncSingleResourceAsync(resourceId, SyncAction.Execute);
        return Json(result);
    }

    [HttpPost("Sync/ExecuteAll/{resourceType}")]
    [Authorize(Roles = RoleNames.Admin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncExecuteAll(GoogleResourceType resourceType)
    {
        var result = await _googleSyncService.SyncResourcesByTypeAsync(resourceType, SyncAction.Execute);
        return Json(result);
    }

    [HttpGet("Summary")]
    [Authorize(Roles = RoleGroups.TeamsAdminBoardOrAdmin)]
    public async Task<IActionResult> Summary()
    {
        var (teams, totalCount) = await _teamService.GetAllTeamsForAdminAsync(1, 500);

        AdminTeamViewModel ToViewModel(Team t, bool isChild) => new()
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
            GoogleGroupEmail = t.GoogleGroupEmail,
            DriveResourceCount = t.GoogleResources.Count(r => r.ResourceType != GoogleResourceType.Group && r.IsActive),
            RoleSlotCount = t.RoleDefinitions.Sum(r => r.SlotCount),
            CreatedAt = t.CreatedAt.ToDateTimeUtc(),
            IsChildTeam = isChild
        };

        // Build hierarchy: system teams first, then user teams ordered with children below parents
        var childIds = teams.Where(t => t.ParentTeamId.HasValue).Select(t => t.Id).ToHashSet();
        var ordered = new List<AdminTeamViewModel>();
        foreach (var t in teams)
        {
            if (t.ParentTeamId.HasValue)
                continue; // children are inserted after their parent below

            ordered.Add(ToViewModel(t, false));

            // Insert child teams directly after their parent
            var children = teams
                .Where(c => c.ParentTeamId == t.Id)
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var child in children)
            {
                ordered.Add(ToViewModel(child, true));
            }
        }

        var viewModel = new AdminTeamListViewModel
        {
            Teams = ordered
        };

        return View(viewModel);
    }

    [HttpGet("Create")]
    [Authorize(Roles = RoleGroups.TeamsAdminBoardOrAdmin)]
    public async Task<IActionResult> CreateTeam(CancellationToken cancellationToken)
    {
        var model = new CreateTeamViewModel
        {
            EligibleParents = await GetEligibleParentTeamsAsync(excludeTeamId: null, cancellationToken)
        };
        return View(model);
    }

    [HttpPost("Create")]
    [Authorize(Roles = RoleGroups.TeamsAdminBoardOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTeam(CreateTeamViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            var team = await _teamService.CreateTeamAsync(model.Name, model.Description, model.RequiresApproval, model.ParentTeamId, model.GoogleGroupPrefix);
            var currentUser = await GetCurrentUserAsync();
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
                    await _teamService.UpdateTeamAsync(team.Id, team.Name, team.Description, team.RequiresApproval, team.IsActive, team.ParentTeamId, googleGroupPrefix: null);
                    SetSuccess(string.Format(_localizer["Admin_TeamCreated"].Value, team.Name));
                    SetError($"Team created but Google Group setup failed: {ex.Message}. The group prefix has been cleared.");
                    return RedirectToAction(nameof(Summary));
                }
            }

            SetSuccess(string.Format(_localizer["Admin_TeamCreated"].Value, team.Name));
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
    [Authorize(Roles = RoleGroups.TeamsAdminBoardOrAdmin)]
    public async Task<IActionResult> EditTeam(Guid id, CancellationToken cancellationToken)
    {
        var team = await _teamService.GetTeamByIdAsync(id, cancellationToken);
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
            Slug = team.Slug,
            RequiresApproval = team.RequiresApproval,
            IsActive = team.IsActive,
            IsSystemTeam = team.IsSystemTeam,
            ParentTeamId = team.ParentTeamId,
            EligibleParents = await GetEligibleParentTeamsAsync(excludeTeamId: id, cancellationToken)
        };

        return View(viewModel);
    }

    [HttpPost("{id:guid}/Edit")]
    [Authorize(Roles = RoleGroups.TeamsAdminBoardOrAdmin)]
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
            await _teamService.UpdateTeamAsync(id, model.Name, model.Description, model.RequiresApproval, model.IsActive, model.ParentTeamId, model.GoogleGroupPrefix);
            var currentUser = await GetCurrentUserAsync();
            _logger.LogInformation("Admin {AdminId} updated team {TeamId}", currentUser?.Id, id);

            // Handles prefix set, changed, or cleared (deactivates old resource if needed)
            try
            {
                await _googleSyncService.EnsureTeamGroupAsync(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync Google Group for team {TeamId}", id);
                SetSuccess(_localizer["Admin_TeamUpdated"].Value);
                SetError($"Team updated but Google Group setup failed: {ex.Message}");
                return RedirectToAction(nameof(Summary));
            }

            SetSuccess(_localizer["Admin_TeamUpdated"].Value);
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
    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTeam(Guid id)
    {
        try
        {
            await _teamService.DeleteTeamAsync(id);
            var currentUser = await GetCurrentUserAsync();
            _logger.LogInformation("Admin {AdminId} deactivated team {TeamId}", currentUser?.Id, id);

            SetSuccess(_localizer["Admin_TeamDeactivated"].Value);
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Summary));
    }

    private async Task<List<Microsoft.AspNetCore.Mvc.Rendering.SelectListItem>> GetEligibleParentTeamsAsync(
        Guid? excludeTeamId, CancellationToken cancellationToken)
    {
        var allTeams = await _teamService.GetAllTeamsAsync(cancellationToken);
        return allTeams
            .Where(t => t.IsActive && !t.IsSystemTeam
                && t.ParentTeamId == null  // Can't nest >1 level
                && t.Id != excludeTeamId)  // Can't be own parent
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(t.Name, t.Id.ToString()))
            .ToList();
    }

}

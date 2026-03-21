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
        var directory = await _teamService.GetTeamDirectoryAsync(user?.Id);

        ViewBag.CanViewSync = RoleChecks.IsTeamsAdminBoardOrAdmin(User);

        var viewModel = new TeamIndexViewModel
        {
            MyTeams = directory.MyTeams.Select(MapTeamSummary).ToList(),
            Departments = directory.Departments.Select(MapTeamSummary).ToList(),
            SystemTeams = directory.SystemTeams.Select(MapTeamSummary).ToList(),
            CanCreateTeam = directory.CanCreateTeam,
            IsAuthenticated = directory.IsAuthenticated
        };

        return View(viewModel);
    }

    private static TeamSummaryViewModel MapTeamSummary(TeamDirectorySummary team) => new()
    {
        Id = team.Id,
        Name = team.Name,
        Description = team.Description,
        Slug = team.Slug,
        MemberCount = team.MemberCount,
        IsSystemTeam = team.IsSystemTeam,
        RequiresApproval = team.RequiresApproval,
        IsPublicPage = team.IsPublicPage,
        IsCurrentUserMember = team.IsCurrentUserMember,
        IsCurrentUserCoordinator = team.IsCurrentUserCoordinator,
        ParentTeamName = team.ParentTeamName,
        ParentTeamSlug = team.ParentTeamSlug
    };

    [AllowAnonymous]
    [HttpGet("{slug}")]
    public async Task<IActionResult> Details(string slug)
    {
        var user = await GetCurrentUserAsync();
        var teamDetail = await _teamService.GetTeamDetailAsync(slug, user?.Id);
        if (teamDetail == null)
        {
            return NotFound();
        }

        var team = teamDetail.Team;

        string? pageContentHtml = null;
        if (!string.IsNullOrEmpty(team.PageContent))
        {
            var sanitizer = new Ganss.Xss.HtmlSanitizer();
            pageContentHtml = sanitizer.Sanitize(Markdig.Markdown.ToHtml(team.PageContent));
        }

        string? pageContentUpdatedByDisplayName = null;
        if (team.PageContentUpdatedByUserId.HasValue)
        {
            var editor = await FindUserByIdAsync(team.PageContentUpdatedByUserId.Value);
            pageContentUpdatedByDisplayName = editor?.DisplayName;
        }

        var memberUserIds = teamDetail.Members.Select(m => m.UserId).ToList();
        var profilesWithCustomPictures = memberUserIds.Count > 0
            ? await _profileService.GetCustomPictureInfoByUserIdsAsync(memberUserIds)
            : [];
        var customPictureByUserId = profilesWithCustomPictures.ToDictionary(
            p => p.UserId,
            p => Url.Action(nameof(ProfileController.Picture), "Profile", new { id = p.ProfileId, v = p.UpdatedAtTicks })!);

        if (!teamDetail.IsAuthenticated)
        {
            var anonymousViewModel = new TeamDetailViewModel
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
                Members = teamDetail.Members.Select(member => new TeamMemberViewModel
                {
                    UserId = member.UserId,
                    DisplayName = member.DisplayName,
                    ProfilePictureUrl = member.ProfilePictureUrl,
                    HasCustomProfilePicture = customPictureByUserId.ContainsKey(member.UserId),
                    CustomProfilePictureUrl = customPictureByUserId.GetValueOrDefault(member.UserId),
                    Role = member.Role.ToString(),
                    IsCoordinator = member.Role == TeamMemberRole.Coordinator
                }).ToList(),
                ParentTeam = team.ParentTeam,
                ChildTeams = teamDetail.ChildTeams
            };

            return View(anonymousViewModel);
        }

        var googleResources = await _teamResourceService.GetTeamResourcesAsync(team.Id);

        ShiftsSummaryCardViewModel? shiftsSummary = null;
        if (team.ParentTeamId == null && team.SystemTeamType == SystemTeamType.None)
        {
            var es = await _shiftMgmt.GetActiveAsync();
            if (es != null)
            {
                var summaryData = await _shiftMgmt.GetShiftsSummaryAsync(es.Id, team.Id);
                if (summaryData != null)
                {
                    var canManageShifts = ShiftRoleChecks.CanManageDepartment(User) ||
                        await _shiftMgmt.IsDeptCoordinatorAsync(user!.Id, team.Id);
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

        var viewModel = new TeamDetailViewModel
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
            CanEditPageContent = teamDetail.CanCurrentUserManage,
            RoleDefinitions = teamDetail.RoleDefinitions.Select(TeamRoleDefinitionViewModel.FromEntity).ToList(),
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
            Members = teamDetail.Members.Select(member => new TeamMemberViewModel
            {
                UserId = member.UserId,
                DisplayName = member.DisplayName,
                Email = member.Email ?? string.Empty,
                ProfilePictureUrl = member.ProfilePictureUrl,
                HasCustomProfilePicture = customPictureByUserId.ContainsKey(member.UserId),
                CustomProfilePictureUrl = customPictureByUserId.GetValueOrDefault(member.UserId),
                Role = member.Role.ToString(),
                JoinedAt = member.JoinedAt.ToDateTimeUtc(),
                IsCoordinator = member.Role == TeamMemberRole.Coordinator
            }).ToList(),
            ParentTeam = team.ParentTeam,
            ChildTeams = teamDetail.ChildTeams,
            IsCurrentUserMember = teamDetail.IsCurrentUserMember,
            IsCurrentUserCoordinator = teamDetail.IsCurrentUserCoordinator,
            CanCurrentUserJoin = teamDetail.CanCurrentUserJoin,
            CanCurrentUserLeave = teamDetail.CanCurrentUserLeave,
            CanCurrentUserManage = teamDetail.CanCurrentUserManage,
            CanCurrentUserEditTeam = teamDetail.CanCurrentUserEditTeam,
            CurrentUserPendingRequestId = teamDetail.CurrentUserPendingRequestId,
            PendingRequestCount = teamDetail.PendingRequestCount,
            ShiftsSummary = shiftsSummary
        };

        return View(viewModel);
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
        var roster = await _teamService.GetRosterAsync(priority, status, period);

        var slots = roster.Select(slot => new RosterSlotViewModel
        {
            TeamName = slot.TeamName,
            TeamSlug = slot.TeamSlug,
            RoleName = slot.RoleName,
            RoleDescription = slot.RoleDescription,
            RoleDefinitionId = slot.RoleDefinitionId,
            SlotNumber = slot.SlotNumber,
            Priority = slot.Priority,
            PriorityBadgeClass = slot.PriorityBadgeClass,
            Period = slot.Period,
            IsFilled = slot.IsFilled,
            AssignedUserName = slot.AssignedUserName
        }).ToList();

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

        var membershipVMs = (await _teamService.GetMyTeamMembershipsAsync(user.Id))
            .Select(m => new MyTeamMembershipViewModel
        {
            TeamId = m.TeamId,
            TeamName = m.TeamName,
            TeamSlug = m.TeamSlug,
            IsSystemTeam = m.IsSystemTeam,
            Role = m.Role.ToString(),
            IsCoordinator = m.Role == TeamMemberRole.Coordinator,
            JoinedAt = m.JoinedAt.ToDateTimeUtc(),
            CanLeave = m.CanLeave,
            PendingRequestCount = m.PendingRequestCount
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
        var result = await _teamService.GetAdminTeamListAsync(1, 500);

        var viewModel = new AdminTeamListViewModel
        {
            Teams = result.Teams.Select(MapAdminTeamSummary).ToList()
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

    private static AdminTeamViewModel MapAdminTeamSummary(AdminTeamSummary team) => new()
    {
        Id = team.Id,
        Name = team.Name,
        Slug = team.Slug,
        IsActive = team.IsActive,
        RequiresApproval = team.RequiresApproval,
        IsSystemTeam = team.IsSystemTeam,
        SystemTeamType = team.SystemTeamType,
        MemberCount = team.MemberCount,
        PendingRequestCount = team.PendingRequestCount,
        HasMailGroup = team.HasMailGroup,
        GoogleGroupEmail = team.GoogleGroupEmail,
        DriveResourceCount = team.DriveResourceCount,
        RoleSlotCount = team.RoleSlotCount,
        CreatedAt = team.CreatedAt.ToDateTimeUtc(),
        IsChildTeam = team.IsChildTeam
    };

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

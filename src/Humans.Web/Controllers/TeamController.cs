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
using Humans.Web.Helpers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Identity;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Teams")]
public class TeamController : HumansControllerBase
{
    private readonly ITeamService _teamService;
    private readonly ITeamPageService _teamPageService;
    private readonly IProfileService _profileService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly ISystemTeamSync _systemTeamSync;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TeamController> _logger;
    private readonly IClock _clock;

    public TeamController(
        ITeamService teamService,
        ITeamPageService teamPageService,
        UserManager<User> userManager,
        IProfileService profileService,
        IGoogleSyncService googleSyncService,
        ISystemTeamSync systemTeamSync,
        IStringLocalizer<SharedResource> localizer,
        IConfiguration configuration,
        IClock clock,
        ILogger<TeamController> logger)
        : base(userManager)
    {
        _teamService = teamService;
        _teamPageService = teamPageService;
        _profileService = profileService;
        _googleSyncService = googleSyncService;
        _systemTeamSync = systemTeamSync;
        _localizer = localizer;
        _configuration = configuration;
        _clock = clock;
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
        var teamPage = await _teamPageService.GetTeamPageDetailAsync(
            slug,
            user?.Id,
            ShiftRoleChecks.CanManageDepartment(User));
        if (teamPage is null)
        {
            return NotFound();
        }

        var team = teamPage.Team;

        string? pageContentHtml = null;
        if (!string.IsNullOrEmpty(team.PageContent))
        {
            var sanitizer = new Ganss.Xss.HtmlSanitizer();
            pageContentHtml = sanitizer.Sanitize(Markdig.Markdown.ToHtml(team.PageContent));
        }

        var customPictureByUserId = BuildCustomPictureUrlsByUserId(teamPage.Members);

        var viewModel = new TeamDetailViewModel
        {
            Id = team.Id,
            Name = team.DisplayName,
            Description = team.Description,
            Slug = team.Slug,
            IsActive = team.IsActive,
            RequiresApproval = team.RequiresApproval,
            IsSystemTeam = team.IsSystemTeam,
            SystemTeamType = team.SystemTeamType != SystemTeamType.None ? team.SystemTeamType : null,
            CreatedAt = team.CreatedAt.ToDateTimeUtc(),
            IsPublicPage = team.IsPublicPage,
            ShowCoordinatorsOnPublicPage = team.ShowCoordinatorsOnPublicPage,
            PageContent = team.PageContent,
            PageContentHtml = pageContentHtml,
            CallsToAction = team.CallsToAction ?? [],
            PageContentUpdatedAt = team.PageContentUpdatedAt?.ToDateTimeUtc(),
            PageContentUpdatedByDisplayName = teamPage.PageContentUpdatedByDisplayName,
            IsAuthenticated = teamPage.IsAuthenticated,
            CanEditPageContent = teamPage.CanCurrentUserManage,
            RoleDefinitions = teamPage.RoleDefinitions.Select(TeamRoleDefinitionViewModel.FromEntity).ToList(),
            Resources = teamPage.Resources.Select(MapTeamResource).ToList(),
            Members = teamPage.Members.Select(member => MapTeamMember(member, customPictureByUserId)).ToList(),
            ParentTeam = team.ParentTeam,
            ChildTeams = teamPage.ChildTeams,
            IsCurrentUserMember = teamPage.IsCurrentUserMember,
            IsCurrentUserCoordinator = teamPage.IsCurrentUserCoordinator,
            CanCurrentUserJoin = teamPage.CanCurrentUserJoin,
            CanCurrentUserLeave = teamPage.CanCurrentUserLeave,
            CanCurrentUserManage = teamPage.CanCurrentUserManage,
            CanCurrentUserEditTeam = teamPage.CanCurrentUserEditTeam,
            CurrentUserPendingRequestId = teamPage.CurrentUserPendingRequestId,
            PendingRequestCount = teamPage.PendingRequestCount,
            ShiftsSummary = MapShiftsSummary(teamPage.ShiftsSummary, slug)
        };

        // Subteam member rollup: for departments, show child team members not already direct members
        if (teamPage.IsAuthenticated && teamPage.ChildTeams.Any())
        {
            var directMemberUserIds = new HashSet<Guid>(viewModel.Members.Select(m => m.UserId));
            var addedUserIds = new HashSet<Guid>();

            foreach (var child in teamPage.ChildTeams)
            {
                var childMembers = await _teamService.GetTeamMembersAsync(child.Id);
                foreach (var cm in childMembers)
                {
                    if (directMemberUserIds.Contains(cm.UserId) || !addedUserIds.Add(cm.UserId))
                        continue;

                    viewModel.ChildTeamMembers.Add(new ChildTeamMemberViewModel
                    {
                        UserId = cm.UserId,
                        DisplayName = cm.User.DisplayName,
                        ProfilePictureUrl = cm.User.ProfilePictureUrl,
                        ChildTeamName = child.Name,
                        ChildTeamSlug = child.Slug
                    });
                }
            }

            // Resolve custom profile pictures for child team members
            if (viewModel.ChildTeamMembers.Count > 0)
            {
                var effectiveUrls = await ProfilePictureUrlHelper.BuildEffectiveUrlsAsync(
                    _profileService, Url,
                    viewModel.ChildTeamMembers.Select(m => (m.UserId, m.ProfilePictureUrl)));

                foreach (var member in viewModel.ChildTeamMembers)
                {
                    if (effectiveUrls.TryGetValue(member.UserId, out var effectiveUrl))
                        member.ProfilePictureUrl = effectiveUrl;
                }
            }
        }

        return View(viewModel);
    }

    private Dictionary<Guid, string> BuildCustomPictureUrlsByUserId(
        IReadOnlyList<TeamPageMemberSummary> members)
    {
        var profileControllerName = nameof(ProfileController)
            .Replace("Controller", string.Empty, StringComparison.Ordinal);

        return members
            .Where(member => member.CustomPicture is not null)
            .ToDictionary(
                member => member.UserId,
                member => Url.Action(
                    nameof(ProfileController.Picture),
                    profileControllerName,
                    new
                    {
                        id = member.CustomPicture!.ProfileId,
                        v = member.CustomPicture.UpdatedAtTicks
                    })!);
    }

    private static TeamResourceLinkViewModel MapTeamResource(TeamPageResourceSummary resource) => new()
    {
        Name = resource.Name,
        Url = resource.Url,
        IconClass = resource.ResourceType switch
        {
            GoogleResourceType.DriveFolder => "fa-solid fa-folder",
            GoogleResourceType.DriveFile => "fa-solid fa-file",
            GoogleResourceType.SharedDrive => "fa-solid fa-hard-drive",
            GoogleResourceType.Group => "fa-solid fa-users",
            _ => "fa-solid fa-link"
        }
    };

    private static TeamMemberViewModel MapTeamMember(
        TeamPageMemberSummary member,
        IReadOnlyDictionary<Guid, string> customPictureByUserId) => new()
        {
            UserId = member.UserId,
            DisplayName = member.DisplayName,
            Email = member.Email ?? string.Empty,
            ProfilePictureUrl = member.ProfilePictureUrl,
            HasCustomProfilePicture = customPictureByUserId.ContainsKey(member.UserId),
            CustomProfilePictureUrl = customPictureByUserId.GetValueOrDefault(member.UserId),
            Role = member.Role,
            JoinedAt = member.JoinedAt?.ToDateTimeUtc() ?? default,
            IsCoordinator = member.Role == TeamMemberRole.Coordinator
        };

    private ShiftsSummaryCardViewModel? MapShiftsSummary(TeamPageShiftsSummary? summary, string slug)
    {
        if (summary is null)
        {
            return null;
        }

        return new ShiftsSummaryCardViewModel
        {
            TotalSlots = summary.TotalSlots,
            ConfirmedCount = summary.ConfirmedCount,
            PendingCount = summary.PendingCount,
            UniqueVolunteerCount = summary.UniqueVolunteerCount,
            ShiftsUrl = Url.Action(nameof(ShiftAdminController.Index), "ShiftAdmin", new { slug })!,
            CanManageShifts = summary.CanManageShifts
        };
    }

    [HttpGet("Birthdays")]
    public async Task<IActionResult> Birthdays(int? month)
    {
        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var currentZone = HttpContext.Session.GetUserTimeZone();
        var currentMonth = month ?? _clock.GetCurrentInstant().InZone(currentZone).Month;
        if (currentMonth < 1 || currentMonth > 12)
            currentMonth = _clock.GetCurrentInstant().InZone(currentZone).Month;

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
            Priority = Enum.TryParse<SlotPriority>(slot.Priority, out var sp) ? sp : SlotPriority.None,
            PriorityBadgeClass = slot.PriorityBadgeClass,
            Period = Enum.TryParse<RolePeriod>(slot.Period, out var rp) ? rp : RolePeriod.Event,
            IsFilled = slot.IsFilled,
            AssignedUserId = slot.AssignedUserId,
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
                Role = m.Role,
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
        if (team is null)
        {
            return NotFound();
        }

        if (team.IsSystemTeam)
        {
            SetError(_localizer["Team_CannotJoinSystem"].Value);
            return RedirectToAction(nameof(Details), new { slug });
        }

        // Hidden teams cannot be joined by non-admin users
        if (team.IsHidden && !RoleChecks.IsTeamsAdminBoardOrAdmin(User))
        {
            return NotFound();
        }

        var isMember = await _teamService.IsUserMemberOfTeamAsync(team.Id, user.Id);
        if (isMember)
        {
            SetError(_localizer["Team_AlreadyMember"].Value);
            return RedirectToAction(nameof(Details), new { slug });
        }

        var pendingRequest = await _teamService.GetUserPendingRequestAsync(team.Id, user.Id);
        if (pendingRequest is not null)
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
        if (team is null)
        {
            return NotFound();
        }

        if (team.Id != model.TeamId)
        {
            return BadRequest();
        }

        if (team.IsHidden && !RoleChecks.IsTeamsAdminBoardOrAdmin(User))
        {
            return NotFound();
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
            _logger.LogWarning(ex, "Failed to join team {TeamId} for user {UserId}", team.Id, user.Id);
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
        if (team is null)
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
            _logger.LogWarning(ex, "Failed to leave team {TeamId} for user {UserId}", team.Id, user.Id);
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
            _logger.LogWarning(ex, "Failed to withdraw join request {RequestId} for user {UserId}", id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(MyTeams));
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
            await PopulateEligibleParentsAsync(model, excludeTeamId: null);
            return View(model);
        }

        try
        {
            var team = await _teamService.CreateTeamAsync(model.Name, model.Description, model.RequiresApproval, model.ParentTeamId, model.GoogleGroupPrefix, model.IsHidden);
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
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to create team: {Message}", ex.Message);
            SetError(ex.Message);
            await PopulateEligibleParentsAsync(model, excludeTeamId: null);
            return View(model);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Failed to create team with Google group prefix {GoogleGroupPrefix}", model.GoogleGroupPrefix);
            ModelState.AddModelError("GoogleGroupPrefix", "This Google Group prefix is already in use by another team.");
            await PopulateEligibleParentsAsync(model, excludeTeamId: null);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating team");
            ModelState.AddModelError("", _localizer["Admin_TeamCreateError"].Value);
            await PopulateEligibleParentsAsync(model, excludeTeamId: null);
            return View(model);
        }
    }

    [HttpGet("{id:guid}/Edit")]
    [Authorize(Roles = RoleGroups.TeamsAdminBoardOrAdmin)]
    public async Task<IActionResult> EditTeam(Guid id, CancellationToken cancellationToken)
    {
        var team = await _teamService.GetTeamByIdAsync(id, cancellationToken);
        if (team is null)
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
            CustomSlug = team.CustomSlug,
            RequiresApproval = team.RequiresApproval,
            IsActive = team.IsActive,
            IsSystemTeam = team.IsSystemTeam,
            HasBudget = team.HasBudget,
            IsHidden = team.IsHidden,
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
            await PopulateEligibleParentsAsync(model, id);
            return View(model);
        }

        try
        {
            await _teamService.UpdateTeamAsync(id, model.Name, model.Description, model.RequiresApproval, model.IsActive, model.ParentTeamId, model.GoogleGroupPrefix, model.CustomSlug, model.HasBudget, model.IsHidden);
            var currentUser = await GetCurrentUserAsync();
            _logger.LogInformation("Admin {AdminId} updated team {TeamId}", currentUser?.Id, id);

            // Handles prefix set, changed, or cleared (deactivates old resource if needed)
            try
            {
                var groupResult = await _googleSyncService.EnsureTeamGroupAsync(id);
                if (!groupResult.Success)
                {
                    if (groupResult.RequiresConfirmation)
                    {
                        SetSuccess(_localizer["Admin_TeamUpdated"].Value);
                        SetError(groupResult.WarningMessage ?? "Confirmation required for group reactivation.");
                        return RedirectToAction(nameof(Summary));
                    }
                    SetSuccess(_localizer["Admin_TeamUpdated"].Value);
                    SetError(groupResult.ErrorMessage ?? "Google Group linking failed.");
                    return RedirectToAction(nameof(Summary));
                }
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
            _logger.LogWarning(ex, "Failed to update team {TeamId}", id);
            ModelState.AddModelError("", ex.Message);
            await PopulateEligibleParentsAsync(model, id);
            return View(model);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Failed to update team {TeamId}", id);
            var message = ex.InnerException?.Message ?? "";
            if (message.Contains("CustomSlug", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError("CustomSlug", "This custom slug is already in use by another team.");
            }
            else
            {
                ModelState.AddModelError("GoogleGroupPrefix", "This Google Group prefix is already in use by another team.");
            }
            await PopulateEligibleParentsAsync(model, id);
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
            _logger.LogWarning(ex, "Failed to deactivate team {TeamId}", id);
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
        SystemTeamType = Enum.TryParse<SystemTeamType>(team.SystemTeamType, out var stt) ? stt : null,
        MemberCount = team.MemberCount,
        PendingRequestCount = team.PendingRequestCount,
        HasMailGroup = team.HasMailGroup,
        GoogleGroupEmail = team.GoogleGroupEmail,
        DriveResourceCount = team.DriveResourceCount,
        RoleSlotCount = team.RoleSlotCount,
        CreatedAt = team.CreatedAt.ToDateTimeUtc(),
        IsChildTeam = team.IsChildTeam,
        PendingShiftSignupCount = team.PendingShiftSignupCount,
        IsHidden = team.IsHidden
    };

    private async Task<List<Microsoft.AspNetCore.Mvc.Rendering.SelectListItem>> GetEligibleParentTeamsAsync(
        Guid? excludeTeamId, CancellationToken cancellationToken)
    {
        var allTeams = await _teamService.GetAllTeamsAsync(cancellationToken);
        return allTeams
            .Where(t => t.IsActive && !t.IsSystemTeam
                && t.ParentTeamId is null  // Can't nest >1 level
                && t.Id != excludeTeamId)  // Can't be own parent
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(t.Name, t.Id.ToString()))
            .ToList();
    }

    private async Task PopulateEligibleParentsAsync(TeamFormViewModelBase model, Guid? excludeTeamId)
    {
        model.EligibleParents = await GetEligibleParentTeamsAsync(excludeTeamId, CancellationToken.None);
    }

}

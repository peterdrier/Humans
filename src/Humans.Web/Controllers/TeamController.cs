#pragma warning disable CS0618 // TeamMember.User / TeamJoinRequest.User — populated in-memory by TeamService (§6b).
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Humans.Application.Configuration;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Teams")]
public class TeamController(
    ITeamService teamService,
    ITeamPageService teamPageService,
    IUserServiceRead userService,
    INotificationService notificationService,
    IGoogleSyncService googleSyncService,
    ITeamResourceService teamResourceService,
    IStringLocalizer<SharedResource> localizer,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry,
    IClock clock,
    ILogger<TeamController> logger) : HumansControllerBase(userService)
{
    private readonly IUserServiceRead _userService = userService;
    private readonly INotificationService _notificationService = notificationService;

    [AllowAnonymous]
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        var hasProfile = User.HasClaim(
            RoleAssignmentClaimsTransformation.HasProfileClaimType,
            RoleAssignmentClaimsTransformation.ActiveClaimValue);
        var directory = await teamService.GetTeamDirectoryAsync(hasProfile ? user?.Id : null, ct);

        var viewModel = new TeamIndexViewModel
        {
            MyTeams = directory.MyTeams
                .OrderBy(t => t.SortKey, StringComparer.OrdinalIgnoreCase)
                .Select(MapTeamSummary)
                .ToList(),
            Departments = directory.Departments
                .OrderBy(
                    t => directory.IsAuthenticated ? t.SortKey : t.Name,
                    StringComparer.OrdinalIgnoreCase)
                .Select(MapTeamSummary)
                .ToList(),
            SystemTeams = directory.SystemTeams
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(MapTeamSummary)
                .ToList(),
            HiddenTeams = directory.HiddenTeams
                .OrderBy(t => t.SortKey, StringComparer.OrdinalIgnoreCase)
                .Select(MapTeamSummary)
                .ToList(),
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
        IsHidden = team.IsHidden,
        RequiresApproval = team.RequiresApproval,
        IsPublicPage = team.IsPublicPage,
        IsCurrentUserMember = team.IsCurrentUserMember,
        IsCurrentUserCoordinator = team.IsCurrentUserCoordinator,
        ParentTeamName = team.ParentTeamName,
        ParentTeamSlug = team.ParentTeamSlug
    };

    [AllowAnonymous]
    [HttpGet("{slug}")]
    public async Task<IActionResult> Details(string slug, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        var hasProfile = User.HasClaim(
            RoleAssignmentClaimsTransformation.HasProfileClaimType,
            RoleAssignmentClaimsTransformation.ActiveClaimValue);
        var effectiveUserId = hasProfile ? user?.Id : null;
        var teamPage = await teamPageService.GetTeamPageDetailAsync(
            slug,
            effectiveUserId,
            ShiftRoleChecks.CanManageDepartment(User),
            ct);
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

        var members = teamPage.Members
            .Select(MapTeamMember)
            .ToList();

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
            CallsToAction = team.CallsToAction,
            PageContentUpdatedAt = team.PageContentUpdatedAt?.ToDateTimeUtc(),
            PageContentUpdatedByDisplayName = teamPage.PageContentUpdatedByDisplayName,
            IsAuthenticated = teamPage.IsAuthenticated,
            CanEditPageContent = teamPage.CanCurrentUserManage,
            RoleDefinitions = teamPage.RoleDefinitions
                .Select(role => TeamRoleDefinitionViewModel.FromSnapshot(role, members))
                .ToList(),
            Resources = teamPage.Resources.Select(MapTeamResource).ToList(),
            Members = members,
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
            ShiftsSummary = MapShiftsSummary(teamPage.ShiftsSummary, slug),
            CanOpenStore = (teamPage.IsCurrentUserCoordinator && team.ParentTeam is null && team.IsActive)
                || RoleChecks.IsAdmin(User)
                || RoleChecks.IsTeamsAdmin(User)
        };

        // Subteam member rollup: for departments, show child team members not already direct members
        if (teamPage.IsAuthenticated && teamPage.ChildTeams.Any())
        {
            var directMemberUserIds = new HashSet<Guid>(viewModel.Members.Select(m => m.UserId));
            var addedUserIds = new HashSet<Guid>();

            var childTeamIds = teamPage.ChildTeams.Select(c => c.Id).ToList();
            var managementRolesByTeam = await teamService.GetManagementRoleNamesByTeamIdsAsync(childTeamIds);

            var teamsById = await teamService.GetTeamsAsync();
            var childMembersByTeam = childTeamIds
                .Where(teamsById.ContainsKey)
                .ToDictionary(
                    id => id,
                    id => teamsById[id].Members
                        .Select(m => new TeamActiveMemberSnapshot(
                            id, m.TeamMemberId, m.UserId,
                            m.DisplayName, m.Email, m.ProfilePictureUrl,
                            m.GoogleEmailStatus, m.Role, m.JoinedAt))
                        .ToList());

            foreach (var child in teamPage.ChildTeams)
            {
                if (!childMembersByTeam.TryGetValue(child.Id, out var childMembers))
                    continue;
                var managementRoleName = managementRolesByTeam.GetValueOrDefault(child.Id);

                foreach (var cm in childMembers)
                {
                    var isCoordinator = cm.Role == TeamMemberRole.Coordinator;

                    if (isCoordinator)
                    {
                        viewModel.SubteamLeads.Add(new ChildTeamMemberViewModel
                        {
                            UserId = cm.UserId,
                            ChildTeamName = child.Name,
                            ChildTeamSlug = child.Slug,
                            IsCoordinator = true,
                            RoleTitle = managementRoleName
                        });
                    }

                    if (directMemberUserIds.Contains(cm.UserId) || !addedUserIds.Add(cm.UserId))
                        continue;

                    viewModel.ChildTeamMembers.Add(new ChildTeamMemberViewModel
                    {
                        UserId = cm.UserId,
                        ChildTeamName = child.Name,
                        ChildTeamSlug = child.Slug,
                        IsCoordinator = isCoordinator,
                        RoleTitle = isCoordinator ? managementRoleName : null
                    });
                }
            }
        }

        return View(viewModel);
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

    private static TeamMemberViewModel MapTeamMember(TeamPageMemberSummary member) => new()
    {
        UserId = member.UserId,
        Email = member.Email ?? string.Empty,
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
            CanManageShifts = summary.CanManageShifts,
            IncludesSubTeamCount = summary.IncludesSubTeamCount
        };
    }

    [HttpGet("Birthdays")]
    public async Task<IActionResult> Birthdays(int? month, CancellationToken ct)
    {
        var (currentUserError, _) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var currentZone = HttpContext.Session.GetUserTimeZone();
        var currentMonth = month ?? clock.GetCurrentInstant().InZone(currentZone).Month;
        if (currentMonth < 1 || currentMonth > 12)
            currentMonth = clock.GetCurrentInstant().InZone(currentZone).Month;

        var profilesWithBirthdays = (await _userService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
            .Where(u => u.Profile is { IsApproved: true, State: not ProfileState.Suspended }
                        && u.Profile.BirthdayMonth == currentMonth
                        && u.Profile.BirthdayDay.HasValue)
            .OrderBy(u => u.Profile!.BirthdayDay)
            .Select(u => new BirthdayProfileInfo(
                u.Id,
                u.BurnerName,
                u.ProfilePictureUrl,
                u.Profile!.HasCustomPicture,
                u.Profile.Id,
                u.Profile.BirthdayDay!.Value,
                u.Profile.BirthdayMonth!.Value))
            .ToList();

        var userIds = profilesWithBirthdays.Select(p => p.UserId).ToList();
        var userIdSet = userIds.ToHashSet();
        var teamsById = await teamService.GetTeamsAsync(ct);
        var teamsByUser = new Dictionary<Guid, List<string>>();
        foreach (var team in teamsById.Values
            .Where(t => t.IsActive && t.SystemTeamType == SystemTeamType.None && !t.IsHidden))
        {
            foreach (var member in team.Members.Where(m => userIdSet.Contains(m.UserId)))
            {
                if (!teamsByUser.TryGetValue(member.UserId, out var names))
                {
                    names = [];
                    teamsByUser[member.UserId] = names;
                }
                names.Add(team.Name);
            }
        }

        var monthName = new DateTime(2000, currentMonth, 1).ToString("MMMM", CultureInfo.CurrentCulture);

        var viewModel = new BirthdayCalendarViewModel
        {
            CurrentMonth = currentMonth,
            CurrentMonthName = monthName,
            Birthdays = profilesWithBirthdays.Select(p => new BirthdayEntryViewModel
            {
                UserId = p.UserId,
                DayOfMonth = p.Day,
                Month = p.Month,
                MonthName = monthName,
                TeamNames = teamsByUser.GetValueOrDefault(p.UserId, [])
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpGet("Roster")]
    public async Task<IActionResult> Roster(string? priority, string? status, string? period, CancellationToken ct = default)
    {
        var roster = await teamService.GetRosterAsync(priority, status, period, ct);

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
    public async Task<IActionResult> Map(CancellationToken ct)
    {
        var profiles = (await _userService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
            .Where(u => u.Profile is { IsApproved: true, Latitude: not null, Longitude: not null, State: not ProfileState.Suspended })
            .Select(u => new LocationProfileInfo(
                u.Id,
                u.BurnerName,
                u.ProfilePictureUrl,
                u.Profile!.Latitude!.Value,
                u.Profile.Longitude!.Value,
                u.Profile.City,
                u.Profile.CountryCode))
            .ToList();

        var markers = profiles.Select(p => new MapMarkerViewModel
        {
            UserId = p.UserId,
            DisplayName = p.DisplayName,
            ProfilePictureUrl = p.ProfilePictureUrl,
            Latitude = p.Latitude,
            Longitude = p.Longitude,
            City = p.City,
            CountryCode = p.CountryCode
        }).ToList();

        ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(
            configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);

        return View(new MapViewModel { Markers = markers });
    }

    [HttpGet("My")]
    public async Task<IActionResult> MyTeams(CancellationToken ct)
    {
        var (currentUserError, user) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (currentUserError is not null)
        {
            return currentUserError;
        }

        var membershipVMs = (await teamService.GetMyTeamMembershipsAsync(user.Id, ct))
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

        var team = await teamService.GetTeamEntityBySlugAsync(slug);
        if (team is null)
        {
            return NotFound();
        }

        if (team.IsSystemTeam)
        {
            SetError(localizer["Team_CannotJoinSystem"].Value);
            return RedirectToAction(nameof(Details), new { slug });
        }

        if (team.IsHidden && !RoleChecks.IsTeamsAdminBoardOrAdmin(User))
        {
            return NotFound();
        }

        var teamInfo = await teamService.GetTeamAsync(team.Id);
        var isMember = teamInfo is { IsActive: true } && teamInfo.Members.Any(m => m.UserId == user.Id);
        if (isMember)
        {
            SetError(localizer["Team_AlreadyMember"].Value);
            return RedirectToAction(nameof(Details), new { slug });
        }

        var pendingRequest = await teamService.GetUserPendingRequestAsync(team.Id, user.Id);
        if (pendingRequest is not null)
        {
            SetError(localizer["Team_AlreadyPendingRequest"].Value);
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

        var team = await teamService.GetTeamEntityBySlugAsync(slug);
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
                await teamService.RequestToJoinTeamAsync(team.Id, user.Id, model.Message);
                SetSuccess(localizer["Team_JoinRequestSubmitted"].Value);
            }
            else
            {
                await teamService.JoinTeamDirectlyAsync(team.Id, user.Id);
                SetSuccess(localizer["Team_Joined"].Value);
            }

            return RedirectToAction(nameof(Details), new { slug });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to join team {TeamId} for user {UserId}", team.Id, user.Id);
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

        var team = await teamService.GetTeamEntityBySlugAsync(slug);
        if (team is null)
        {
            return NotFound();
        }

        try
        {
            await teamService.LeaveTeamAsync(team.Id, user.Id);
            SetSuccess(localizer["Team_Left"].Value);
            return RedirectToAction(nameof(Index));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to leave team {TeamId} for user {UserId}", team.Id, user.Id);
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
            await teamService.WithdrawJoinRequestAsync(id, user.Id);
            SetSuccess(localizer["Team_RequestWithdrawn"].Value);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to withdraw join request {RequestId} for user {UserId}", id, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(MyTeams));
    }

    [HttpGet("Summary")]
    [Authorize(Policy = PolicyNames.TeamsAdminBoardOrAdmin)]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var result = await teamService.GetAdminTeamListAsync(1, 500, ct);

        var viewModel = new AdminTeamListViewModel();

        // Parent-then-children flat list; children inherit the parent's bucket.
        List<AdminTeamViewModel>? currentBucket = null;
        foreach (var team in result.Teams.Select(MapAdminTeamSummary))
        {
            if (!team.IsChildTeam)
            {
                currentBucket = team.IsHidden ? viewModel.Hidden
                              : team.IsSystemTeam ? viewModel.System
                              : viewModel.Departments;
            }

            (currentBucket ?? viewModel.Departments).Add(team);
        }

        return View(viewModel);
    }

    [HttpGet("Create")]
    [Authorize(Policy = PolicyNames.TeamsAdminBoardOrAdmin)]
    public async Task<IActionResult> CreateTeam(CancellationToken cancellationToken)
    {
        var model = new CreateTeamViewModel
        {
            EligibleParents = await GetEligibleParentTeamsAsync(excludeTeamId: null, cancellationToken)
        };
        return View(model);
    }

    [HttpPost("Create")]
    [Authorize(Policy = PolicyNames.TeamsAdminBoardOrAdmin)]
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
            var team = await teamService.CreateTeamAsync(model.Name, model.Description, model.RequiresApproval, model.ParentTeamId, model.GoogleGroupPrefix, model.IsHidden);
            var currentUser = await GetCurrentUserInfoAsync();
            logger.LogInformation("Admin {AdminId} created team {TeamId} ({TeamName})", currentUser?.Id, team.Id, team.Name);

            if (!string.IsNullOrEmpty(model.GoogleGroupPrefix))
            {
                try
                {
                    await googleSyncService.EnsureTeamGroupAsync(team.Id);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to create Google Group for new team {TeamId}, clearing prefix", team.Id);
                    await teamService.UpdateTeamAsync(team.Id, team.Name, team.Description, team.RequiresApproval, team.IsActive, team.ParentTeamId, googleGroupPrefix: null);
                    SetSuccess(string.Format(localizer["Admin_TeamCreated"].Value, team.Name));
                    SetError($"Team created but Google Group setup failed: {ex.Message}. The group prefix has been cleared.");
                    return RedirectToAction(nameof(Summary));
                }
            }

            SetSuccess(string.Format(localizer["Admin_TeamCreated"].Value, team.Name));
            return RedirectToAction(nameof(Summary));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to create team: {Message}", ex.Message);
            SetError(ex.Message);
            await PopulateEligibleParentsAsync(model, excludeTeamId: null);
            return View(model);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Failed to create team with Google group prefix {GoogleGroupPrefix}", model.GoogleGroupPrefix);
            ModelState.AddModelError("GoogleGroupPrefix", "This Google Group prefix is already in use by another team.");
            await PopulateEligibleParentsAsync(model, excludeTeamId: null);
            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating team");
            ModelState.AddModelError("", localizer["Admin_TeamCreateError"].Value);
            await PopulateEligibleParentsAsync(model, excludeTeamId: null);
            return View(model);
        }
    }

    [HttpGet("{id:guid}/Edit")]
    [Authorize(Policy = PolicyNames.TeamsAdminBoardOrAdmin)]
    public async Task<IActionResult> EditTeam(Guid id, CancellationToken cancellationToken)
    {
        var team = await teamService.GetTeamByIdAsync(id, cancellationToken);
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
            IsSensitive = team.IsSensitive,
            IsPromotedToDirectory = team.IsPromotedToDirectory,
            ParentTeamId = team.ParentTeamId,
            EligibleParents = await GetEligibleParentTeamsAsync(excludeTeamId: id, cancellationToken)
        };

        return View(viewModel);
    }

    [HttpPost("{id:guid}/Edit")]
    [Authorize(Policy = PolicyNames.TeamsAdminBoardOrAdmin)]
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
            await teamService.UpdateTeamAsync(id, model.Name, model.Description, model.RequiresApproval, model.IsActive, model.ParentTeamId, model.GoogleGroupPrefix, model.CustomSlug, model.HasBudget, model.IsHidden, model.IsSensitive, model.IsPromotedToDirectory);
            var currentUser = await GetCurrentUserInfoAsync();
            logger.LogInformation("Admin {AdminId} updated team {TeamId}", currentUser?.Id, id);

            try
            {
                var groupResult = await googleSyncService.EnsureTeamGroupAsync(id);
                if (!groupResult.Success)
                {
                    if (groupResult.RequiresConfirmation)
                    {
                        SetSuccess(localizer["Admin_TeamUpdated"].Value);
                        SetError(groupResult.WarningMessage ?? "Confirmation required for group reactivation.");
                        return RedirectToAction(nameof(Summary));
                    }
                    SetSuccess(localizer["Admin_TeamUpdated"].Value);
                    SetError(groupResult.ErrorMessage ?? "Google Group linking failed.");
                    return RedirectToAction(nameof(Summary));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to sync Google Group for team {TeamId}", id);
                SetSuccess(localizer["Admin_TeamUpdated"].Value);
                SetError($"Team updated but Google Group setup failed: {ex.Message}");
                return RedirectToAction(nameof(Summary));
            }

            SetSuccess(localizer["Admin_TeamUpdated"].Value);
            return RedirectToAction(nameof(Summary));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to update team {TeamId}", id);
            ModelState.AddModelError("", ex.Message);
            await PopulateEligibleParentsAsync(model, id);
            return View(model);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Failed to update team {TeamId}", id);
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
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTeam(Guid id)
    {
        try
        {
            await teamService.DeleteTeamAsync(id);
            var currentUser = await GetCurrentUserInfoAsync();
            logger.LogInformation("Admin {AdminId} deactivated team {TeamId}", currentUser?.Id, id);

            SetSuccess(localizer["Admin_TeamDeactivated"].Value);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to deactivate team {TeamId}", id);
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

    [HttpGet("{teamId:guid}/GoogleResources")]
    [Authorize(Policy = PolicyNames.TeamsAdminBoardOrAdmin)]
    public async Task<IActionResult> GetTeamGoogleResources(Guid teamId, CancellationToken cancellationToken)
    {
        var resources = await teamResourceService.GetTeamResourcesAsync(teamId, cancellationToken);
        var result = resources.Select(r => new
        {
            name = r.Name,
            type = r.ResourceType switch
            {
                GoogleResourceType.DriveFolder => "Drive Folder",
                GoogleResourceType.SharedDrive => "Shared Drive",
                GoogleResourceType.Group => "Google Group",
                GoogleResourceType.DriveFile => "Drive File",
                _ => r.ResourceType.ToString()
            }
        });
        return Json(result);
    }

    private async Task<List<Microsoft.AspNetCore.Mvc.Rendering.SelectListItem>> GetEligibleParentTeamsAsync(
        Guid? excludeTeamId, CancellationToken cancellationToken)
    {
        var allTeams = await teamService.GetAllTeamsAsync(cancellationToken);
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

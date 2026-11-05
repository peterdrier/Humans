using Humans.Users.Services;
// @e2e: board.spec.ts
// @e2e: profile.spec.ts
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Base.Enums;
using Humans.Users.Models;
using Humans.Camps.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;
using NodaTime;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Base.Configuration;
using Microsoft.Extensions.Configuration;

namespace Humans.Users.Controllers;

[Authorize]
[Route("Profile")]
internal sealed class ProfileViewController(
    IUserManagement userService,
    IProfilePictureService profilePictureService,
    IShiftSignups shiftSignupService,
    IShiftService shiftMgmt,
    IStringLocalizer<SharedResource> sharedLocalizer,
    ITeamService teamService,
    ICampService campService,
    IGovernanceService governanceService,
    IAuthorizationService authorizationService,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry,
    IClock clock) : HumansControllerBase(userService)
{
    private readonly IUserManagement _userService = userService;

    [HttpGet("Picture")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> Picture(Guid id, CancellationToken ct)
    {
        // The picture service owns the file read and the GDPR gate.
        var result = await profilePictureService.GetProfilePictureAsync(id, ct);
        if (result is null)
        {
            return NotFound();
        }

        return File(result.Value.Data, result.Value.ContentType);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> ViewProfile(Guid id, CancellationToken ct)
    {
        var profileInfo = await _userService.GetUserInfoAsync(id, ct);
        var profile = profileInfo?.Profile;

        if (profile is null || profileInfo!.IsSuspended)
        {
            return NotFound();
        }

        // A link to a merged-away id lands on the survivor's page, under the survivor's URL.
        if (profileInfo.Id != id)
            return RedirectToAction(nameof(ViewProfile), new { id = profileInfo.Id });

        var viewer = await GetCurrentUserInfoAsync(ct);
        if (viewer is null)
        {
            return NotFound();
        }

        var isOwnProfile = viewer.Id == id;
        var viewerIsCoordinator = !isOwnProfile
            && (await shiftMgmt.GetCoordinatorTeamIdsAsync(viewer.Id, ct)).Count > 0;

        var noShowContext = await BuildNoShowHistoryContextAsync(id, isOwnProfile, viewerIsCoordinator, ct);

        var canViewSentMessages = await CanViewSentMessagesAsync(isOwnProfile, viewerIsCoordinator);

        var viewModel = new ProfileViewModel
        {
            Id = profile.Id,
            UserId = id,
            PageTitle = profileInfo.BurnerName,
            IsOwnProfile = isOwnProfile,
            NoShowHistory = noShowContext.History,
            CanViewShiftSignups = noShowContext.CanView,
            CanViewSentMessages = canViewSentMessages,
        };

        return View("~/Views/Profile/Index.cshtml", viewModel);
    }

    private async Task<(bool CanView, List<NoShowHistoryItem>? History)> BuildNoShowHistoryContextAsync(
        Guid profileUserId,
        bool isOwnProfile,
        bool viewerIsCoordinator,
        CancellationToken ct)
    {
        if (isOwnProfile)
        {
            return (false, null);
        }

        var viewerCanViewShiftHistory = viewerIsCoordinator || ShiftsRoles.IsPrivilegedSignupApprover(User);
        if (!viewerCanViewShiftHistory)
        {
            return (false, null);
        }

        var noShows = await shiftSignupService.GetNoShowHistoryAsync(profileUserId, ct);
        if (noShows.Count == 0)
        {
            return (true, null);
        }

        var noShowTeamIds = noShows.Select(s => s.TeamId).Distinct().ToList();
        var teamsById = await teamService.GetTeamsAsync(ct);
        var noShowTeamNames = noShowTeamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);

        return (true, noShows.OrderByDescending(s => s.ReviewedAt).Select(s =>
        {
            var signupTz = DateTimeZoneProviders.Tzdb[s.TimeZoneId];
            var zoned = s.ShiftStart.InZone(signupTz);
            return new NoShowHistoryItem
            {
                ShiftLabel = s.ShiftLabel,
                DepartmentName = noShowTeamNames.GetValueOrDefault(s.TeamId, ""),
                ShiftDateLabel = zoned.ToDateTimeUnspecified().ToMonthDayTime(),
                MarkedByUserId = s.ReviewedByUserId,
                MarkedAtLabel = s.ReviewedAt?.InZone(signupTz).ToDateTimeUnspecified().ToMonthDayTime()
            };
        }).ToList());
    }

    /// <summary>
    /// A coordinator or privileged shift role, never on one's own profile. Same coordinator check as
    /// <see cref="BuildNoShowHistoryContextAsync"/>, so the two panels share access rules.
    /// </summary>
    private async Task<bool> CanViewSentMessagesAsync(bool isOwnProfile, bool viewerIsCoordinator)
    {
        if (isOwnProfile)
            return false;

        var isPrivilegedApprover = (await authorizationService.AuthorizeAsync(User, ShiftsPolicies.PrivilegedSignupApprover)).Succeeded;
        return viewerIsCoordinator || isPrivilegedApprover;
    }

    [HttpGet("{id:guid}/Popover")]
    public async Task<IActionResult> Popover(Guid id, CancellationToken ct)
    {
        var info = await _userService.GetUserInfoAsync(id, ct);
        if (info?.Profile is null) return NotFound();
        id = info.Id; // memberships and camp below are keyed by the live id

        var memberships = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive && t.SystemTeamType != SystemTeamType.Volunteers)
            .Select(t => new { TeamInfo = t, Membership = t.Members.FirstOrDefault(m => m.UserId == id) })
            .Where(x => x.Membership is not null)
            .Select(x => new TeamMembership(x.TeamInfo.Name, x.Membership!.Role) { IsHidden = x.TeamInfo.IsHidden })
            .ToList();
        // Rendered admin-only in the view.
        var camp = await campService.GetCampUserInfoAsync(id, ct);
        var tier = await governanceService.GetMembershipTierAsync(id, ct);
        var vm = ProfileSummaryViewModelBuilder.BuildWithProfile(info, memberships, tier, camp);

        return PartialView("_HumanPopover", vm);
    }

    // For anonymous viewers of public team pages: renders only for an active coordinator of a team that publishes
    // its coordinators, and 404s otherwise so anonymous probes can't enumerate users.
    [AllowAnonymous]
    [HttpGet("{id:guid}/PublicPopover")]
    public async Task<IActionResult> PublicPopover(Guid id, CancellationToken ct)
    {
        var info = await _userService.GetUserInfoAsync(id, ct);
        if (info is null) return NotFound();
        id = info.Id;

        var roleLabels = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive
                && !t.IsHidden
                && t.IsPublicPage
                && t.ShowCoordinatorsOnPublicPage
                && t.Members.Any(m => m.UserId == id && m.Role == TeamMemberRole.Coordinator))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => $"{sharedLocalizer["Profile_Coordinator"].Value} · {t.Name}")
            .ToList();

        if (roleLabels.Count == 0) return NotFound();

        var vm = new PublicPopoverViewModel
        {
            UserId = info.Id,
            RoleLabels = roleLabels
        };

        return PartialView("_HumanPopoverPublic", vm);
    }

    [HttpGet("Search")]
    public async Task<IActionResult> Search(string? q, CancellationToken ct)
    {
        var viewModel = new HumanSearchViewModel { Query = q };

        if (!q.HasSearchTerm())
        {
            return View(viewModel);
        }

        // Never the Admin bit here. Uncapped, so relevance ranking can surface the right person.
        var results = await _userService.SearchUsersAsync(
            q, PersonSearchFields.PublicAll, limit: int.MaxValue, ct);

        viewModel.Results = results
            .OrderByRelevance()
            .ToList();

        return View(viewModel);
    }

    [HttpGet("Birthdays")]
    public async Task<IActionResult> Birthdays(int? month, CancellationToken ct)
    {
        var thisMonth = clock.GetCurrentInstant().InZone(HttpContext.Session.GetUserTimeZone()).Month;
        var currentMonth = month is >= 1 and <= 12 ? month.Value : thisMonth;

        var people = (await _userService.GetAllUserInfosAsync(ct))
            .Where(u => !u.IsSuspended
                        && u.Profile is { BirthdayDay: not null }
                        && u.Profile.BirthdayMonth == currentMonth)
            .OrderBy(u => u.Profile!.BirthdayDay)
            .ToList();

        var ids = people.Select(u => u.Id).ToHashSet();
        var teamsByUser = new Dictionary<Guid, List<string>>();
        foreach (var team in (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive && t.SystemTeamType == SystemTeamType.None && !t.IsHidden))
        {
            foreach (var member in team.Members.Where(m => ids.Contains(m.UserId)))
            {
                if (!teamsByUser.TryGetValue(member.UserId, out var names))
                {
                    names = [];
                    teamsByUser[member.UserId] = names;
                }
                names.Add(team.Name);
            }
        }

        return View(new BirthdayCalendarViewModel
        {
            CurrentMonth = currentMonth,
            CurrentMonthName = new DateTime(2000, currentMonth, 1).ToMonthName(),
            Birthdays = people.Select(u => new BirthdayEntryViewModel
            {
                UserId = u.Id,
                DayOfMonth = u.Profile!.BirthdayDay!.Value,
                TeamNames = teamsByUser.GetValueOrDefault(u.Id, [])
            }).ToList()
        });
    }

    [HttpGet("Map")]
    public async Task<IActionResult> Map(CancellationToken ct)
    {
        var markers = (await _userService.GetAllUserInfosAsync(ct))
            .Where(u => !u.IsSuspended && u.Profile is { Latitude: not null, Longitude: not null })
            .Select(u => new MapMarkerViewModel
            {
                UserId = u.Id,
                DisplayName = u.BurnerName,
                ProfilePictureUrl = u.ProfilePictureUrl,
                Latitude = u.Profile!.Latitude!.Value,
                Longitude = u.Profile.Longitude!.Value,
                City = u.Profile.City,
                CountryCode = u.Profile.CountryCode
            })
            .ToList();

        ViewData["GoogleMapsApiKey"] = configuration.GetRequiredSetting(
            configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);

        return View(new MapViewModel { Markers = markers });
    }
}

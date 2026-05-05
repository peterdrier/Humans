using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Admin-only catalog of every reusable UI widget — TagHelpers, ViewComponents, and
/// shared partials — rendered against real data so designers and developers can see
/// what exists, what it's called, and how it looks filled in. Companion to
/// <c>/ColorPalette</c>. Admin dev tool — no nav link, access via URL directly.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("WidgetGallery")]
public sealed class WidgetGalleryController : HumansControllerBase
{
    private readonly ITeamService _teamService;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly ILogger<WidgetGalleryController> _logger;

    public WidgetGalleryController(
        UserManager<User> userManager,
        ITeamService teamService,
        IShiftManagementService shiftMgmt,
        ILogger<WidgetGalleryController> logger)
        : base(userManager)
    {
        _teamService = teamService;
        _shiftMgmt = shiftMgmt;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var (error, currentUser) = await RequireCurrentUserAsync();
        if (error is not null)
            return error;

        var sampleTeam = await ResolveSampleTeamAsync();
        var sampleVolunteerProfile = await TryGetVolunteerProfileAsync(currentUser.Id);
        var (eventSettings, sampleRota, staffingData, staffingHours) = await ResolveShiftsSamplesAsync();

        var displayName = string.IsNullOrEmpty(currentUser.DisplayName)
            ? currentUser.UserName ?? "Current user"
            : currentUser.DisplayName;

        var model = new WidgetGalleryViewModel
        {
            CurrentUserId = currentUser.Id,
            CurrentUserDisplayName = displayName,
            SampleTeamId = sampleTeam?.Id,
            SampleTeamSlug = sampleTeam?.Slug,
            SampleTeamName = sampleTeam?.Name,
            SampleVolunteerProfile = sampleVolunteerProfile,
            SampleEventSettings = eventSettings,
            SampleRota = sampleRota,
            SampleStaffingData = staffingData,
            SampleStaffingHours = staffingHours,
            SampleShiftsSummary = new ShiftsSummaryCardViewModel
            {
                TotalSlots = 24,
                ConfirmedCount = 17,
                PendingCount = 3,
                UniqueVolunteerCount = 12,
                ShiftsUrl = Url.Action(nameof(ShiftsController.Index), "Shifts") ?? "#",
                CanManageShifts = true,
                IncludesSubTeamCount = 2,
            },
            SamplePager = new PagerViewModel
            {
                CurrentPage = 3,
                TotalPages = 8,
                Action = "Index",
                Window = 2,
            },
            SampleProfileSummary = new ProfileSummaryViewModel
            {
                UserId = currentUser.Id,
                DisplayName = displayName,
                Email = currentUser.Email,
                MembershipStatus = "Active",
                MembershipTier = "Volunteer",
                IsSuspended = false,
                PreferredLanguage = currentUser.PreferredLanguage,
                Teams = sampleTeam is null ? new() : new() { sampleTeam.Name },
            },
        };

        return View(model);
    }

    private async Task<Team?> ResolveSampleTeamAsync()
    {
        var allTeams = await _teamService.GetAllTeamsAsync();
        return allTeams
            .Where(t => !t.IsSystemTeam && !t.IsHidden)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? allTeams.FirstOrDefault();
    }

    private async Task<VolunteerEventProfile?> TryGetVolunteerProfileAsync(Guid userId)
    {
        try
        {
            return await _shiftMgmt.GetShiftProfileAsync(userId, includeMedical: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to fetch shift profile for user {UserId}: {Reason}", userId, ex.Message);
            return null;
        }
    }

    private async Task<(EventSettings?, Rota?, IReadOnlyList<DailyStaffingData>, IReadOnlyList<DailyStaffingHours>)> ResolveShiftsSamplesAsync()
    {
        try
        {
            var es = await _shiftMgmt.GetActiveAsync();
            if (es is null)
                return (null, null, Array.Empty<DailyStaffingData>(), Array.Empty<DailyStaffingHours>());

            Rota? rota = null;
            var depts = await _shiftMgmt.GetDepartmentsWithRotasAsync(es.Id);
            if (depts.Count > 0)
            {
                var rotas = await _shiftMgmt.GetRotasByDepartmentAsync(depts[0].TeamId, es.Id);
                rota = rotas.FirstOrDefault();
            }

            var staffing = await _shiftMgmt.GetStaffingDataAsync(es.Id);
            var hours = await _shiftMgmt.GetStaffingHoursAsync(es.Id);
            return (es, rota, staffing, hours);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to resolve shifts samples for widget gallery: {Reason}", ex.Message);
            return (null, null, Array.Empty<DailyStaffingData>(), Array.Empty<DailyStaffingHours>());
        }
    }
}

public sealed class WidgetGalleryViewModel
{
    public required Guid CurrentUserId { get; init; }
    public required string CurrentUserDisplayName { get; init; }
    public Guid? SampleTeamId { get; init; }
    public string? SampleTeamSlug { get; init; }
    public string? SampleTeamName { get; init; }
    public VolunteerEventProfile? SampleVolunteerProfile { get; init; }
    public EventSettings? SampleEventSettings { get; init; }
    public Rota? SampleRota { get; init; }
    public required IReadOnlyList<DailyStaffingData> SampleStaffingData { get; init; }
    public required IReadOnlyList<DailyStaffingHours> SampleStaffingHours { get; init; }
    public required ShiftsSummaryCardViewModel SampleShiftsSummary { get; init; }
    public required PagerViewModel SamplePager { get; init; }
    public required ProfileSummaryViewModel SampleProfileSummary { get; init; }
}

using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

/// <summary>
/// Admin-only catalog of every reusable UI widget — TagHelpers, ViewComponents, and
/// shared partials — rendered against real data so designers and developers can see
/// what exists, what it's called, and how it looks filled in. Companion to
/// <c>/ColorPalette</c>. Admin dev tool — linked from the admin sidebar "Design" group.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("WidgetGallery")]
public sealed class WidgetGalleryController(
    IUserServiceRead userService,
    ITeamServiceRead teamService,
    IShiftManagementService shiftMgmt,
    ILogger<WidgetGalleryController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var (error, currentUser) = await RequireCurrentUserAsync();
        if (error is not null)
            return error;

        var sampleTeam = await ResolveSampleTeamAsync();
        var sampleVolunteerProfile = await TryGetVolunteerProfileAsync(currentUser.Id);
        var shifts = await ResolveShiftsSamplesAsync(currentUser.Id);

        var displayName = string.IsNullOrEmpty(currentUser.BurnerName)
            ? "Current user"
            : currentUser.BurnerName;

        var model = new WidgetGalleryViewModel
        {
            CurrentUserId = currentUser.Id,
            CurrentUserDisplayName = displayName,
            SampleTeamId = sampleTeam?.Id,
            SampleTeamSlug = sampleTeam?.Slug,
            SampleTeamName = sampleTeam?.Name,
            SampleVolunteerProfile = sampleVolunteerProfile,
            SampleEventSettings = shifts.EventSettings,
            SampleRota = shifts.Rota,
            SampleStaffingData = shifts.StaffingData,
            SampleStaffingHours = shifts.StaffingHours,
            SampleRotaShifts = shifts.RotaShifts,
            SampleUserSignupShiftIds = shifts.UserSignupShiftIds,
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
            SamplePager = new PagerViewModel(totalPages: 8, currentPage: 3, action: "Index"),
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
            SampleHumanSearchResults = new List<HumanSearchResultViewModel>
            {
                new()
                {
                    UserId = currentUser.Id,
                    BurnerName = displayName,
                    ProfilePictureUrl = currentUser.ProfilePictureUrl,
                    MatchField = "Name",
                },
                new()
                {
                    UserId = Guid.NewGuid(),
                    BurnerName = "Sparkle",
                    MatchField = "Bio",
                    MatchSnippet = "...love fire dancing and welding...",
                },
                new()
                {
                    UserId = Guid.NewGuid(),
                    BurnerName = "Embers",
                    MatchField = "Email",
                    MatchedEmail = "embers@example.org",
                    AdminEmail = "embers@example.org",
                    MembershipStatus = "Active",
                    CreatedAt = DateTime.UtcNow.AddMonths(-8),
                    LastLoginAt = DateTime.UtcNow.AddDays(-2),
                    AdminDetailUrl = "#",
                },
            },
        };

        return View(model);
    }

    private async Task<TeamInfo?> ResolveSampleTeamAsync()
    {
        var allTeams = (await teamService.GetTeamsAsync()).Values;
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
            return await shiftMgmt.GetShiftProfileAsync(userId);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to fetch shift profile for user {UserId}: {Reason}", userId, ex.Message);
            return null;
        }
    }

    private async Task<ShiftsSamples> ResolveShiftsSamplesAsync(Guid currentUserId)
    {
        try
        {
            var es = await shiftMgmt.GetActiveAsync();
            if (es is null)
                return ShiftsSamples.Empty;

            Rota? rota = null;
            Guid? sampleDeptId = null;
            var depts = await shiftMgmt.GetDepartmentsWithRotasAsync(es.Id);
            if (depts.Count > 0)
            {
                sampleDeptId = depts[0].TeamId;
                var rotas = await shiftMgmt.GetRotasByDepartmentAsync(sampleDeptId.Value, es.Id);
                rota = rotas.FirstOrDefault();
            }

            var staffing = await shiftMgmt.GetStaffingSnapshotAsync(es.Id);

            var sampleRotaShifts = new List<ShiftDisplayItem>();
            if (rota is not null && sampleDeptId is not null)
            {
                var browse = await shiftMgmt.GetBrowseShiftsAsync(new ShiftBrowseQuery(
                    es.Id,
                    sampleDeptId.Value,
                    Flags: ShiftBrowseQueryFlags.IncludeSignups));
                sampleRotaShifts = browse
                    .Where(u => u.Shift.RotaId == rota.Id)
                    .OrderBy(u => u.Shift.DayOffset)
                    .ThenBy(u => u.Shift.StartTime)
                    .Take(8)
                    .Select(u => MapToDisplayItem(u, es))
                    .ToList();
            }

            var userSignupShiftIds = sampleRotaShifts
                .Where(s => s.Signups.Any(sig => sig.UserId == currentUserId))
                .Select(s => s.Shift.Id)
                .ToHashSet();

            return new ShiftsSamples(es, rota, staffing.StaffingData, staffing.StaffingHours, sampleRotaShifts, userSignupShiftIds);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to resolve shifts samples for widget gallery: {Reason}", ex.Message);
            return ShiftsSamples.Empty;
        }
    }

    private ShiftDisplayItem MapToDisplayItem(UrgentShift u, EventSettings es)
    {
        return new ShiftDisplayItem
        {
            Shift = u.Shift,
            AbsoluteStart = u.Shift.GetAbsoluteStart(es),
            AbsoluteEnd = u.Shift.GetAbsoluteEnd(es),
            Period = u.Shift.GetShiftPeriod(es),
            ConfirmedCount = u.ConfirmedCount,
            RemainingSlots = u.RemainingSlots,
            UrgencyScore = u.UrgencyScore,
            Signups = u.Signups
                .Select(s => new ShiftSignupInfo(s.UserId, s.DisplayName, s.Status))
                .ToList(),
        };
    }

    private sealed record ShiftsSamples(
        EventSettings? EventSettings,
        Rota? Rota,
        IReadOnlyList<DailyStaffingData> StaffingData,
        IReadOnlyList<DailyStaffingHours> StaffingHours,
        IReadOnlyList<ShiftDisplayItem> RotaShifts,
        IReadOnlySet<Guid> UserSignupShiftIds)
    {
        public static readonly ShiftsSamples Empty = new(
            null, null,
            [],
            [],
            [],
            new HashSet<Guid>());
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
    public required IReadOnlyList<ShiftDisplayItem> SampleRotaShifts { get; init; }
    public required IReadOnlySet<Guid> SampleUserSignupShiftIds { get; init; }
    public required ShiftsSummaryCardViewModel SampleShiftsSummary { get; init; }
    public required PagerViewModel SamplePager { get; init; }
    public required ProfileSummaryViewModel SampleProfileSummary { get; init; }
    public required IReadOnlyList<HumanSearchResultViewModel> SampleHumanSearchResults { get; init; }
}

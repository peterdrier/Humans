using Humans.Users.Models;
using Humans.Shifts.Contracts;
using Humans.Tickets.Contracts;
using Humans.Teams.Contracts;
using Humans.UI.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

using Humans.UI.Authorization;
using Humans.UI.Models;
using Humans.Users.Contracts;

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
    IShiftManagementServiceRead shiftMgmt,
    IShiftVolunteerProfiles shiftProfiles,
    IBurnSettingsService burnSettings,
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
        var shifts = await ResolveShiftsSamplesAsync();

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
            SampleStaffingData = shifts.StaffingData,
            SampleStaffingHours = shifts.StaffingHours,
            SampleShiftsSummary = new ShiftsSummaryCardViewModel
            {
                TotalSlots = 24,
                ConfirmedCount = 17,
                PendingCount = 3,
                UniqueVolunteerCount = 12,
                ShiftsUrl = Url.Action("Index", "Shifts") ?? "#",
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
            SampleTableRows =
            [
                new() { Name = "Sparkle", Amount = 120.50m, JoinedAt = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromDays(400)), Status = TicketAttendeeStatus.Valid, IsVip = true },
                new() { Name = "Embers", Amount = 95.00m, JoinedAt = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromDays(120)), Status = TicketAttendeeStatus.CheckedIn, IsVip = false },
                new() { Name = "Dusty", Amount = 240.00m, JoinedAt = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromDays(30)), Status = TicketAttendeeStatus.Void, IsVip = false },
                new() { Name = "Nova", Amount = 95.00m, JoinedAt = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromDays(10)), Status = TicketAttendeeStatus.Valid, IsVip = false },
            ],
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

    private async Task<ShiftVolunteerProfileInfo?> TryGetVolunteerProfileAsync(Guid userId)
    {
        try
        {
            return await shiftProfiles.GetShiftProfileAsync(userId);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to fetch shift profile for user {UserId}: {Reason}", userId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The staffing-chart samples and the active event's name. The rota-shaped samples
    /// moved into Humans.Shifts' ShiftsGalleryViewComponent at that section's G5
    /// (nobodies-collective/Humans#866); what is left binds only leaf records, which is
    /// why the two staffing partials stayed in Shell.
    /// </summary>
    private async Task<ShiftsSamples> ResolveShiftsSamplesAsync()
    {
        try
        {
            var es = await burnSettings.GetActiveAsync(HttpContext.RequestAborted);
            if (es is null)
                return ShiftsSamples.Empty;

            var staffing = await shiftMgmt.GetStaffingSnapshotAsync(es.Id);
            return new ShiftsSamples(es, staffing.StaffingData, staffing.StaffingHours);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to resolve shifts samples for widget gallery: {Reason}", ex.Message);
            return ShiftsSamples.Empty;
        }
    }

    private sealed record ShiftsSamples(
        BurnSettingsInfo? EventSettings,
        IReadOnlyList<DailyStaffingData> StaffingData,
        IReadOnlyList<DailyStaffingHours> StaffingHours)
    {
        public static readonly ShiftsSamples Empty = new(null, [], []);
    }
}

internal sealed class WidgetGalleryViewModel
{
    public required Guid CurrentUserId { get; init; }
    public required string CurrentUserDisplayName { get; init; }
    public Guid? SampleTeamId { get; init; }
    public string? SampleTeamSlug { get; init; }
    public string? SampleTeamName { get; init; }
    public ShiftVolunteerProfileInfo? SampleVolunteerProfile { get; init; }
    public BurnSettingsInfo? SampleEventSettings { get; init; }
    public required IReadOnlyList<DailyStaffingData> SampleStaffingData { get; init; }
    public required IReadOnlyList<DailyStaffingHours> SampleStaffingHours { get; init; }
    public required ShiftsSummaryCardViewModel SampleShiftsSummary { get; init; }
    public required PagerViewModel SamplePager { get; init; }
    public required ProfileSummaryViewModel SampleProfileSummary { get; init; }
    public required IReadOnlyList<HumanSearchResultViewModel> SampleHumanSearchResults { get; init; }
    public List<TableDemoRow> SampleTableRows { get; set; } = [];
}

public sealed class TableDemoRow
{
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public NodaTime.Instant JoinedAt { get; set; }
    public TicketAttendeeStatus Status { get; set; }
    public bool IsVip { get; set; }
}

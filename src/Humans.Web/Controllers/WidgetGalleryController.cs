using Humans.Users.Models;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

using Humans.Base.Authorization;
using Humans.Base.Models;
using Humans.Users.Contracts;

namespace Humans.Web.Controllers;

/// <summary>
/// Admin-only catalog of every reusable UI widget — TagHelpers, ViewComponents, and
/// shared partials — rendered against hard-coded sample data so designers and developers
/// can see what exists, what it's called, and how it looks filled in. Companion to
/// <c>/ColorPalette</c>. Admin dev tool — linked from the admin sidebar "Design" group.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("WidgetGallery")]
public sealed class WidgetGalleryController(IUserServiceRead userService) : HumansControllerBase(userService)
{
    private static readonly Guid SampleTeamId = Guid.NewGuid();
    private const string SampleTeamSlug = "fire-conclave";
    private const string SampleTeamName = "Fire Conclave";

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var (error, currentUser) = await RequireCurrentUserAsync();
        if (error is not null)
            return error;

        var displayName = string.IsNullOrEmpty(currentUser.BurnerName)
            ? "Current user"
            : currentUser.BurnerName;

        var model = new WidgetGalleryViewModel
        {
            CurrentUserId = currentUser.Id,
            CurrentUserDisplayName = displayName,
            SampleTeamId = SampleTeamId,
            SampleTeamSlug = SampleTeamSlug,
            SampleTeamName = SampleTeamName,
            SampleVolunteerProfile = BuildSampleVolunteerProfile(currentUser.Id),
            SampleEventSettings = BuildSampleEventSettings(),
            SampleStaffingData = BuildSampleStaffingData(),
            SampleStaffingHours = BuildSampleStaffingHours(),
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
                Teams = new() { SampleTeamName },
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

    private static ShiftVolunteerProfileInfo BuildSampleVolunteerProfile(Guid userId) => new(
        userId,
        Skills: ["Welding", "First Aid"],
        Quirks: ["Early riser", "Coffee snob"],
        Languages: ["English", "Spanish"]);

    /// <summary>
    /// The staffing-chart samples and the active event's name. The rota-shaped samples
    /// moved into Humans.Shifts' ShiftsGalleryViewComponent at that section's G5
    /// (nobodies-collective/Humans#866); what is left binds only leaf records, which is
    /// why the two staffing partials stayed in Shell.
    /// </summary>
    private static BurnSettingsInfo BuildSampleEventSettings() => new(
        Id: Guid.NewGuid(),
        EventName: "Nowhere 2026",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new LocalDate(2026, 7, 1),
        BuildStartOffset: -7,
        EventEndOffset: 5,
        StrikeEndOffset: 8,
        FirstCrewStartOffset: -10,
        SetupWeekStartOffset: -7,
        PreEventWeekStartOffset: -3,
        FinishingWeekendStartOffset: 6,
        EarlyEntryCapacity: new Dictionary<int, int> { [-10] = 20, [-7] = 50 },
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        IsShiftBrowsingOpen: true);

    private static List<DailyStaffingData> BuildSampleStaffingData() =>
    [
        new(-1, "Day -1", ConfirmedCount: 8, TotalSlots: 12, MinSlots: 6, Period: "Set-up"),
        new(0, "Day 0", ConfirmedCount: 15, TotalSlots: 20, MinSlots: 10, Period: "Event"),
        new(1, "Day 1", ConfirmedCount: 5, TotalSlots: 10, MinSlots: 4, Period: "Strike"),
    ];

    private static List<DailyStaffingHours> BuildSampleStaffingHours() =>
    [
        new(-1, "Day -1", EssentialHours: 12.5, ImportantHours: 8.0, NormalHours: 4.0),
        new(0, "Day 0", EssentialHours: 20.0, ImportantHours: 15.0, NormalHours: 10.0),
        new(1, "Day 1", EssentialHours: 6.0, ImportantHours: 5.0, NormalHours: 2.0),
    ];
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
    public Instant JoinedAt { get; set; }
    public TicketAttendeeStatus Status { get; set; }
    public bool IsVip { get; set; }
}

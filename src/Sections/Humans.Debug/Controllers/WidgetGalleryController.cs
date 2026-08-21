using Humans.Camps.Contracts;
using Humans.Events.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NodaTime;

using Humans.Base.Authorization;
using Humans.Base.Models;
using Humans.Users.Contracts;

namespace Humans.Debug.Controllers;

/// <summary>
/// Admin-only catalog of every reusable UI widget — TagHelpers, ViewComponents, and
/// shared partials — rendered against hard-coded sample data so designers and developers
/// can see what exists, what it's called, and how it looks filled in. Companion to
/// <c>/ColorPalette</c>. Admin dev tool — linked from the admin sidebar "Design" group.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("WidgetGallery")]
internal sealed class WidgetGalleryController(
    IUserServiceRead userService,
    ITeamServiceRead teamService,
    ICampServiceRead campService,
    IShiftManagementServiceRead shiftService,
    IBurnSettingsService burnSettings,
    IEventServiceRead eventService,
    IConfiguration configuration) : HumansControllerBase(userService)
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

        var searchRowKeys = await ResolveSearchRowKeysAsync(HttpContext.RequestAborted);

        var model = new WidgetGalleryViewModel
        {
            SearchRowKeys = searchRowKeys,
            CurrentUserId = currentUser.Id,
            CurrentUserDisplayName = displayName,
            SampleTeamId = SampleTeamId,
            SampleTeamSlug = SampleTeamSlug,
            SampleTeamName = SampleTeamName,
            SampleVolunteerProfile = BuildSampleVolunteerProfile(currentUser.Id),
            SampleEventSettings = BuildSampleEventSettings(),
            SampleStaffingData = BuildSampleStaffingData(),
            SampleStaffingHours = BuildSampleStaffingHours(),
            // ProfileSummaryViewModel samples (_ProfileCard / _HumanPopover) are internal to
            // Humans.Users, so the Users section renders those cards itself via its
            // UsersGallery view component (nobodies-collective/Humans#1091).
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
            // <vc:user-search-result> is keyed by user id and Users resolves the human itself
            // (nobodies-collective/Humans#1062), so the gallery has no sample rows to fabricate —
            // the card renders the signed-in admin with a made-up match snippet.
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

    /// <summary>
    /// One real key per search-result row card. Those four components fetch by key and render
    /// nothing when it does not resolve, so a fabricated sample would show a blank card —
    /// indistinguishable from the unbound-tag failure the gallery is meant to expose
    /// (nobodies-collective/Humans#1062). Every read here is cache-served. A key is null in an
    /// environment holding no such row, and the card says so instead of rendering empty.
    /// </summary>
    private async Task<SearchRowKeys> ResolveSearchRowKeysAsync(CancellationToken ct)
    {
        var teams = await teamService.GetTeamsAsync(ct);

        var campSettings = await campService.GetSettingsAsync(ct);
        var camps = await campService.GetCampsForYearAsync(campSettings.PublicYear, ct);

        // Browse rather than urgency-ranked: the gallery wants any rota, and the urgent list
        // is filtered to what still needs volunteers, so it empties out after the burn.
        var burn = await burnSettings.GetActiveAsync(ct);
        var browsable = burn is null
            ? []
            : await shiftService.GetBrowseShiftsAsync(new ShiftBrowseQuery(burn.Id));

        // Skipped when the feature is off, so the gallery never becomes a second producer
        // of event ids behind the flag's back.
        var events = configuration.GetValue<bool>("Features:Events")
            ? await eventService.GetApprovedEventsAsync(
                campId: null, venueId: null, categoryId: null, q: null, excludedSlugs: [], ct)
            : [];

        return new SearchRowKeys(
            TeamId: teams.Values.FirstOrDefault()?.Id,
            CampId: camps.FirstOrDefault()?.Id,
            RotaId: browsable.FirstOrDefault()?.Rota.Id,
            EventId: events.FirstOrDefault()?.Id);
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
    public required SearchRowKeys SearchRowKeys { get; init; }
    public List<TableDemoRow> SampleTableRows { get; set; } = [];
}

/// <summary>
/// Live keys for the five search-result row cards. Null where this environment holds no
/// such row — the humans row always resolves, so it keys off the signed-in admin instead.
/// </summary>
internal sealed record SearchRowKeys(
    Guid? TeamId,
    Guid? CampId,
    Guid? RotaId,
    Guid? EventId);

internal sealed class TableDemoRow
{
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public Instant JoinedAt { get; set; }
    public TicketAttendeeStatus Status { get; set; }
    public bool IsVip { get; set; }
}

using System.ComponentModel.DataAnnotations;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Enums;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models;

// === EventSettings ===

public class EventSettingsViewModel : IValidatableObject
{
    public Guid? Id { get; set; }

    [Required, MaxLength(256)]
    public string EventName { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string TimeZoneId { get; set; } = "Europe/Madrid";

    [Required]
    public string GateOpeningDate { get; set; } = string.Empty;

    public int BuildStartOffset { get; set; } = -14;
    public int EventEndOffset { get; set; } = 6;
    public int StrikeEndOffset { get; set; } = 9;

    // Build sub-period boundaries — defaults match the entity defaults set by EF config.
    [Range(int.MinValue, -1, ErrorMessage = "First crew start must be a negative offset relative to gate-opening day.")]
    public int FirstCrewStartOffset { get; set; } = -25;

    [Range(int.MinValue, -1, ErrorMessage = "Set-up week start must be a negative offset relative to gate-opening day.")]
    public int SetupWeekStartOffset { get; set; } = -16;

    [Range(int.MinValue, -1, ErrorMessage = "Pre-event week start must be a negative offset relative to gate-opening day.")]
    public int PreEventWeekStartOffset { get; set; } = -9;

    [Range(int.MinValue, -1, ErrorMessage = "Finishing weekend start must be a negative offset relative to gate-opening day.")]
    public int FinishingWeekendStartOffset { get; set; } = -4;

    public string EarlyEntryCapacityJson { get; set; } = "{}";
    public string? BarriosEarlyEntryAllocationJson { get; set; }

    public string? EarlyEntryClose { get; set; }

    public bool IsShiftBrowsingOpen { get; set; }
    public int? GlobalVolunteerCap { get; set; }
    public int ReminderLeadTimeHours { get; set; } = 24;
    public bool IsActive { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FirstCrewStartOffset < BuildStartOffset)
        {
            yield return new ValidationResult(
                $"First crew offset cannot be earlier than build start offset ({nameof(BuildStartOffset)}).",
                [nameof(FirstCrewStartOffset)]);
        }

        if (FirstCrewStartOffset >= SetupWeekStartOffset
            || SetupWeekStartOffset >= PreEventWeekStartOffset
            || PreEventWeekStartOffset >= FinishingWeekendStartOffset)
        {
            yield return new ValidationResult(
                "Build sub-period offsets must be strictly ascending: First crew < Set-up week < Pre-event week < Finishing weekend.",
                [
                    nameof(FirstCrewStartOffset),
                    nameof(SetupWeekStartOffset),
                    nameof(PreEventWeekStartOffset),
                    nameof(FinishingWeekendStartOffset)
                ]);
        }
    }
}

// === Rota ===

public class CreateRotaModel
{
    [Required, MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    public ShiftPriority Priority { get; set; }
    public SignupPolicy Policy { get; set; }
    public RotaPeriod Period { get; set; } = RotaPeriod.Event;

    [MaxLength(2000)]
    public string? PracticalInfo { get; set; }

    /// <summary>
    /// Comma-separated tag IDs to assign to the rota.
    /// </summary>
    public string? TagIds { get; set; }
}

public class EditRotaModel : CreateRotaModel
{
    public Guid RotaId { get; set; }
}

public class MoveRotaModel
{
    public Guid TargetTeamId { get; set; }
}

// === Shift ===

public class CreateShiftModel
{
    public Guid RotaId { get; set; }

    [MaxLength(2000)]
    public string? Description { get; set; }

    public int DayOffset { get; set; }

    [Required]
    public string StartTime { get; set; } = "08:00";

    public double DurationHours { get; set; } = 4;

    public int MinVolunteers { get; set; } = 1;
    public int MaxVolunteers { get; set; } = 5;
    public bool AdminOnly { get; set; }
}

public class EditShiftModel : CreateShiftModel
{
    public Guid ShiftId { get; set; }
}

// === Staffing Grid (Build/Strike) ===

public class StaffingGridModel
{
    public Guid RotaId { get; set; }
    public List<DayStaffingEntry> Days { get; set; } = [];
}

public class DayStaffingEntry
{
    public int DayOffset { get; set; }
    public int MinVolunteers { get; set; } = 2;
    public int MaxVolunteers { get; set; } = 5;
}

// === Generate Event Shifts ===

public class GenerateEventShiftsModel
{
    public int StartDayOffset { get; set; }
    public int EndDayOffset { get; set; }
    public List<TimeSlotEntry> TimeSlots { get; set; } = [];
    public int MinVolunteers { get; set; } = 2;
    public int MaxVolunteers { get; set; } = 5;
}

public class TimeSlotEntry
{
    public string StartTime { get; set; } = "08:00";
    public double DurationHours { get; set; } = 4;
}

// === Browse ===

public class ShiftBrowseViewModel
{
    public EventSettings? EventSettings { get; set; }
    public List<DepartmentShiftGroup> Departments { get; set; } = [];
    public List<DepartmentOption> AllDepartments { get; set; } = [];
    public Guid? FilterDepartmentId { get; set; }
    public string? FilterFromDate { get; set; }
    public string? FilterToDate { get; set; }
    public string? FilterPeriod { get; set; }

    /// <summary>
    /// Active period filters for multiselect phase cards (Build, Event, Strike).
    /// When all three are selected (or none explicitly set), no period filtering is applied.
    /// </summary>
    public List<string> FilterPeriods { get; set; } = [];

    public bool ShowFullShifts { get; set; }
    public Guid UserId { get; set; }
    public HashSet<Guid> UserSignupShiftIds { get; set; } = [];
    public Dictionary<Guid, SignupStatus> UserSignupStatuses { get; set; } = new();
    public bool ShowSignups { get; set; }

    /// <summary>
    /// True when the viewer has no dietary preferences recorded; partials lock out
    /// Sign-Up buttons and the banner view component renders the inline prompt.
    /// </summary>
    public bool SignupsBlockedByMissingDietary { get; set; }

    /// <summary>
    /// Department coverage pies rendered above the page. One row per
    /// top-level department + each promoted sub-team. Empty when the event
    /// has no rotas that contribute pie hours.
    /// </summary>
    public IReadOnlyList<DepartmentCoveragePie> CoveragePies { get; set; } = [];

    /// <summary>
    /// Current sort mode: "urgency" for most-needed-first, null/empty for default by-department grouping.
    /// </summary>
    public string? Sort { get; set; }

    /// <summary>
    /// Flat list of rotas sorted by urgency score (populated only when Sort == "urgency").
    /// </summary>
    public List<RotaShiftGroup> UrgencyRankedRotas { get; set; } = [];

    /// <summary>
    /// All available tags for the filter UI.
    /// </summary>
    public List<ShiftTagSummary> AllTags { get; set; } = [];

    /// <summary>
    /// Currently selected tag IDs for filtering.
    /// </summary>
    public List<Guid> FilterTagIds { get; set; } = [];

    /// <summary>
    /// Tag IDs the current volunteer has selected as preferences (for highlighting).
    /// </summary>
    public HashSet<Guid> UserPreferredTagIds { get; set; } = [];

    /// <summary>
    /// Count of the current user's active signups (confirmed + pending), shown as badge on "My Shifts" tab.
    /// </summary>
    public int MySignupCount { get; set; }

    /// <summary>
    /// All active signups across all events for the current user, projected for the
    /// build/strike conflict-confirmation modal in the dashboard view.
    /// </summary>
    public IReadOnlyList<UserSignupConflictItem> UserActiveSignups { get; set; } = [];
}

public class DepartmentOption
{
    public Guid TeamId { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class DepartmentShiftGroup
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string? TeamDescription { get; set; }
    public string TeamSlug { get; set; } = string.Empty;
    public List<RotaShiftGroup> Rotas { get; set; } = [];
}

public class RotaShiftGroup
{
    public Rota Rota { get; set; } = null!;
    public List<ShiftDisplayItem> Shifts { get; set; } = [];

    /// <summary>Department name, populated for urgency-sorted view where rotas are shown flat.</summary>
    public string? DepartmentName { get; set; }

    /// <summary>Department slug for linking, populated for urgency-sorted view.</summary>
    public string? DepartmentSlug { get; set; }

    /// <summary>Highest urgency score among shifts in this rota (for sorting).</summary>
    public double MaxUrgencyScore { get; set; }

    /// <summary>Total confirmed signups across all shifts in this rota.</summary>
    public int TotalConfirmed { get; set; }

    /// <summary>Total max volunteer slots across all shifts in this rota.</summary>
    public int TotalSlots { get; set; }
}

public record ShiftSignupInfo(Guid UserId, string DisplayName, SignupStatus Status);

public class ShiftDisplayItem
{
    public Shift Shift { get; set; } = null!;
    public Instant AbsoluteStart { get; set; }
    public Instant AbsoluteEnd { get; set; }
    public ShiftPeriod Period { get; set; }
    public int ConfirmedCount { get; set; }
    public int RemainingSlots { get; set; }
    public double UrgencyScore { get; set; }
    public IReadOnlyList<ShiftSignupInfo> Signups { get; set; } = [];
}

// === Mine ===

public class MyShiftsViewModel
{
    public EventSettings? EventSettings { get; set; }
    public Guid UserId { get; set; }
    public List<MySignupItem> Upcoming { get; set; } = [];
    public List<MySignupItem> Pending { get; set; } = [];
    public List<MySignupItem> Past { get; set; } = [];
    public string? ICalUrl { get; set; }
    public List<int> AvailableDayOffsets { get; set; } = [];

    /// <summary>
    /// True when the viewer has no dietary preferences recorded; the banner view
    /// component renders the inline prompt and downstream signup CTAs are locked.
    /// </summary>
    public bool SignupsBlockedByMissingDietary { get; set; }
}

public class MySignupItem
{
    public ShiftSignup Signup { get; set; } = null!;
    public string? RotaName { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public Instant AbsoluteStart { get; set; }
    public Instant AbsoluteEnd { get; set; }
}

// === ShiftAdmin ===

public class ShiftAdminViewModel
{
    public TeamInfo Department { get; set; } = null!;
    public EventSettings EventSettings { get; set; } = null!;
    public List<Rota> Rotas { get; set; } = [];
    public List<ShiftSignup> PendingSignups { get; set; } = [];
    public int TotalSlots { get; set; }
    public int ConfirmedCount { get; set; }
    public bool CanManageShifts { get; set; }
    public bool CanApproveSignups { get; set; }
    public Dictionary<Guid, VolunteerBadgesViewModel> VolunteerProfiles { get; set; } = new();

    /// <summary>
    /// User display data (BurnerName, ProfilePictureUrl) keyed by UserId for every signup
    /// in <see cref="Rotas"/> and <see cref="PendingSignups"/>. Resolved by the controller via
    /// <c>IUserService.GetUserInfosAsync</c>; the view reads from this dictionary instead of
    /// navigating <c>ShiftSignup.User</c> (cross-domain nav, removed per design-rules §6c).
    /// </summary>
    public IReadOnlyDictionary<Guid, UserInfo> Users { get; set; } = new Dictionary<Guid, UserInfo>();

    public bool CanViewMedical { get; set; }
    public List<DailyStaffingData> StaffingData { get; set; } = [];
    public List<DailyStaffingHours> StaffingHours { get; set; } = [];
    public Instant Now { get; set; }
    public List<DepartmentOption> AllDepartments { get; set; } = [];

    /// <summary>
    /// All available tags for the tag picker UI.
    /// </summary>
    public List<ShiftTagSummary> AllTags { get; set; } = [];

    /// <summary>
    /// True when the coordinator has activated the "Incomplete onboarding" filter
    /// chip on the Pending Approvals list (only show signups whose users are
    /// missing required Volunteer consents).
    /// </summary>
    public bool IncompleteOnboardingFilter { get; set; }
}

// === Homepage ===

public class ShiftCardsViewModel
{
    public List<MySignupItem> NextShifts { get; set; } = [];
    public int PendingCount { get; set; }
    public List<UrgentShiftItem> UrgentShifts { get; set; } = [];
}

public class UrgentShiftItem
{
    public Shift Shift { get; set; } = null!;
    public string? RotaName { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public Instant AbsoluteStart { get; set; }
    public int RemainingSlots { get; set; }
    public double UrgencyScore { get; set; }
}

// === Shift Info (user-scoped profile) ===

public class ShiftInfoViewModel
{
    public List<string> SelectedSkills { get; set; } = [];
    public string? SkillOtherText { get; set; }
    public List<string> SelectedQuirks { get; set; } = []; // Toggle quirks only (no time prefs)
    public string? TimePreference { get; set; } // Mutually exclusive: Early Bird, Night Owl, All Day, No Preference
    public List<string> SelectedLanguages { get; set; } = [];
    public string? LanguageOtherText { get; set; }

    // Skill options with emoji prefixes for display
    public static readonly string[] SkillOptions = ["Bartending", "First Aid", "Driving", "Sound", "Electrical", "Construction", "Cooking", "Art", "DJ", "Other"];
    public static readonly string[] LanguageOptions = ["English", "Spanish", "German", "French", "Italian", "Portuguese", "Catalan", "Other"];

    // Time preferences — mutually exclusive, stored as quirk value
    public static readonly string[] TimePreferenceOptions = ["Early Bird", "Night Owl", "All Day", "No Preference"];

    // Toggle quirks — multi-select, separate from time preference
    public static readonly string[] ToggleQuirkOptions = ["Sober Shift", "Work In Shade", "Quiet Work", "Physical Work OK", "No Heights"];

    private static readonly string[] StoredSkillOptions = SkillOptions.Where(s => !string.Equals(s, "Other", StringComparison.Ordinal)).ToArray();
    private static readonly string[] StoredLanguageOptions = LanguageOptions.Where(l => !string.Equals(l, "Other", StringComparison.Ordinal)).ToArray();

    // Emoji maps for view rendering
    public static readonly Dictionary<string, string> SkillEmoji = new(StringComparer.Ordinal)
    {
        ["Bartending"] = "\U0001f378",
        ["Cooking"] = "\U0001f373",
        ["Sound"] = "\U0001f39a\ufe0f",
        ["DJ"] = "\U0001f3a7",
        ["First Aid"] = "\U0001fa7a",
        ["Electrical"] = "\u26a1",
        ["Driving"] = "\U0001f697",
        ["Construction"] = "\U0001f528",
        ["Art"] = "\U0001f3a8",
        ["Other"] = "\u2728"
    };

    public static readonly Dictionary<string, string> LanguageEmoji = new(StringComparer.Ordinal)
    {
        ["English"] = "EN",
        ["Spanish"] = "ES",
        ["French"] = "FR",
        ["German"] = "DE",
        ["Italian"] = "IT",
        ["Portuguese"] = "PT",
        ["Catalan"] = "CA",
        ["Other"] = "\U0001f30d"
    };

    public static readonly Dictionary<string, string> TimePreferenceEmoji = new(StringComparer.Ordinal)
    {
        ["Early Bird"] = "\U0001f305",
        ["Night Owl"] = "\U0001f319",
        ["All Day"] = "\u2600\ufe0f",
        ["No Preference"] = "\U0001f937"
    };

    public static readonly Dictionary<string, string> TimePreferenceDesc = new(StringComparer.Ordinal)
    {
        ["Early Bird"] = "Morning shifts, set up and prep",
        ["Night Owl"] = "Evening and late-night shifts",
        ["All Day"] = "Flexible, morning through evening",
        ["No Preference"] = "I'll take whatever's needed"
    };

    public static ShiftInfoViewModel FromProfile(VolunteerEventProfile? profile)
    {
        var quirks = profile?.Quirks ?? [];
        var skills = profile?.Skills ?? [];
        var languages = profile?.Languages ?? [];

        var viewModel = new ShiftInfoViewModel
        {
            SelectedSkills = skills.Where(s => !s.StartsWith("Other:", StringComparison.Ordinal)).ToList(),
            SkillOtherText = skills.FirstOrDefault(s => s.StartsWith("Other:", StringComparison.Ordinal))?.Substring(6).Trim(),
            SelectedQuirks = ExtractToggleQuirks(quirks),
            TimePreference = ExtractTimePreference(quirks),
            SelectedLanguages = languages.Where(l => !l.StartsWith("Other:", StringComparison.Ordinal)).ToList(),
            LanguageOtherText = languages.FirstOrDefault(l => l.StartsWith("Other:", StringComparison.Ordinal))?.Substring(6).Trim(),
        };

        if (viewModel.SkillOtherText is not null && !viewModel.SelectedSkills.Contains("Other", StringComparer.Ordinal))
            viewModel.SelectedSkills.Add("Other");
        if (viewModel.LanguageOtherText is not null && !viewModel.SelectedLanguages.Contains("Other", StringComparer.Ordinal))
            viewModel.SelectedLanguages.Add("Other");

        return viewModel;
    }

    /// <summary>Extract the time preference value from a flat quirks array.</summary>
    public static string? ExtractTimePreference(List<string> quirks)
        => quirks.FirstOrDefault(q => TimePreferenceOptions.Contains(q, StringComparer.Ordinal));

    /// <summary>Extract toggle quirks (excluding time preferences) from a flat quirks array.</summary>
    public static List<string> ExtractToggleQuirks(List<string> quirks)
        => quirks.Where(q => !TimePreferenceOptions.Contains(q, StringComparer.Ordinal)).ToList();

    /// <summary>Merge a time preference and toggle quirks back into a flat quirks array.</summary>
    public static List<string> MergeQuirks(string? timePreference, List<string> toggleQuirks)
    {
        var result = new List<string>(toggleQuirks);
        if (!string.IsNullOrEmpty(timePreference))
            result.Add(timePreference);
        return result;
    }

    public static List<string> ExtractUnknownSkills(List<string> skills)
        => skills
            .Where(s => !s.StartsWith("Other:", StringComparison.Ordinal) &&
                !StoredSkillOptions.Contains(s, StringComparer.Ordinal))
            .ToList();

    public static List<string> ExtractUnknownLanguages(List<string> languages)
        => languages
            .Where(l => !l.StartsWith("Other:", StringComparison.Ordinal) &&
                !StoredLanguageOptions.Contains(l, StringComparer.Ordinal))
            .ToList();

    public static List<string> ExtractUnknownQuirks(List<string> quirks)
        => quirks
            .Where(q => !TimePreferenceOptions.Contains(q, StringComparer.Ordinal) &&
                !ToggleQuirkOptions.Contains(q, StringComparer.Ordinal))
            .ToList();

    public static List<string> MergeSkills(List<string>? selectedSkills, string? skillOtherText, List<string>? existingSkills)
    {
        var result = new List<string>(selectedSkills ?? []);
        if (result.Contains("Other", StringComparer.Ordinal))
        {
            result.Remove("Other");
            if (!string.IsNullOrWhiteSpace(skillOtherText))
                result.Add($"Other: {skillOtherText.Trim()}");
        }

        result.AddRange(ExtractUnknownSkills(existingSkills ?? []));
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    public static List<string> MergeLanguages(List<string>? selectedLanguages, string? languageOtherText, List<string>? existingLanguages)
    {
        var result = new List<string>(selectedLanguages ?? []);
        if (result.Contains("Other", StringComparer.Ordinal))
        {
            result.Remove("Other");
            if (!string.IsNullOrWhiteSpace(languageOtherText))
                result.Add($"Other: {languageOtherText.Trim()}");
        }

        result.AddRange(ExtractUnknownLanguages(existingLanguages ?? []));
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    public static List<string> MergePersistedQuirks(
        string? timePreference,
        List<string>? selectedQuirks,
        List<string>? existingQuirks)
    {
        var result = MergeQuirks(timePreference, selectedQuirks ?? []);
        result.AddRange(ExtractUnknownQuirks(existingQuirks ?? []));
        return result.Distinct(StringComparer.Ordinal).ToList();
    }
}

// === Dashboard ===

public class ShiftDashboardViewModel
{
    public List<UrgentShift> Shifts { get; set; } = [];
    public List<DepartmentOption> Departments { get; set; } = [];
    public Guid? SelectedDepartmentId { get; set; }
    public Guid? SelectedRotaId { get; set; }
    /// <summary>ISO date string passed via query — round-trips through the form input.</summary>
    public string? SelectedStartDate { get; set; }
    /// <summary>ISO date string passed via query — round-trips through the form input.</summary>
    public string? SelectedEndDate { get; set; }
    /// <summary>Parsed start date if <see cref="SelectedStartDate"/> was a valid ISO date.</summary>
    public LocalDate? FilterStartDate { get; set; }
    /// <summary>Parsed end date if <see cref="SelectedEndDate"/> was a valid ISO date.</summary>
    public LocalDate? FilterEndDate { get; set; }
    public ShiftPeriod? SelectedPeriod { get; set; }
    public BuildSubPeriod? SelectedSubPeriod { get; set; }
    public EventSettings EventSettings { get; set; } = null!;
    public List<DailyStaffingData> StaffingData { get; set; } = [];
    public List<DailyStaffingHours> StaffingHours { get; set; } = [];

    public DashboardOverview? Overview { get; set; }
    public IReadOnlyList<CoordinatorActivityRow> CoordinatorActivity { get; set; } = [];
    public IReadOnlyList<DashboardTrendPoint> Trends { get; set; } = [];
    public IReadOnlyList<DailyDepartmentStaffing> DailyDepartmentStaffing { get; set; } = [];
    public IReadOnlyList<ShiftDurationBreakdownRow> ShiftDurationBreakdown { get; set; } = [];
    public CoverageHeatmap CoverageHeatmap { get; set; } = new([], []);
    public TrendWindow TrendWindow { get; set; } = TrendWindow.Last30Days;
    public bool IsDevelopment { get; set; }
    public BuildDayCountdown Countdown { get; set; } = new(0, LocalDate.MinIsoValue, 0, 0);
}

public record BuildDayCountdown(int DaysToBuild, LocalDate FirstBuildDay, int Weeks, int RemainderDays);

public class VolunteerSearchResult
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Skills { get; set; } = [];
    public List<string> Quirks { get; set; } = [];
    public List<string> Languages { get; set; } = [];
    public string? DietaryPreference { get; set; }
    public int BookedShiftCount { get; set; }
    public bool HasOverlap { get; set; }
    public bool IsInPool { get; set; }
    public string? MedicalConditions { get; set; }
}

// === Shifts Summary Card ===

public class ShiftsSummaryCardViewModel
{
    public int TotalSlots { get; set; }
    public int ConfirmedCount { get; set; }
    public int PendingCount { get; set; }
    public int UniqueVolunteerCount { get; set; }
    public string ShiftsUrl { get; set; } = "";
    public bool CanManageShifts { get; set; }

    /// <summary>
    /// When > 0, indicates this summary includes data from child teams.
    /// </summary>
    public int IncludesSubTeamCount { get; set; }
}

// === Shift Signups ViewComponent ===

public enum ShiftSignupsViewMode
{
    Self,
    Admin
}

public class ShiftSignupsViewModel
{
    public List<MySignupItem> Upcoming { get; set; } = [];
    public List<MySignupItem> Pending { get; set; } = [];
    public List<MySignupItem> Past { get; set; } = [];
    public EventSettings? EventSettings { get; set; }
    public ShiftSignupsViewMode ViewMode { get; set; }
    public Guid UserId { get; set; }
    public string? DisplayName { get; set; }
}

// === Rota Partial View Models ===

public class RotaHeaderViewModel
{
    public Rota Rota { get; set; } = null!;
    public bool ShowPreferenceStar { get; set; }
}

public record UserSignupConflictItem(
    LocalDate Date,
    string RotaName,
    Instant AbsoluteStart,
    Instant AbsoluteEnd,
    string DisplayStart,
    string DisplayEnd);

public record ShiftWindow(Instant AbsoluteStart, Instant AbsoluteEnd);

public class BuildStrikeRotaTableViewModel
{
    public RotaShiftGroup RotaGroup { get; set; } = null!;
    public EventSettings EventSettings { get; set; } = null!;
    public HashSet<Guid> UserSignupShiftIds { get; set; } = [];
    public bool ShowSignups { get; set; }

    /// <summary>
    /// True when the viewer has no dietary preferences recorded; the date-range
    /// Sign-Up form is rendered disabled with the locked-out copy.
    /// </summary>
    public bool SignupsBlockedByMissingDietary { get; set; }

    public Guid? FilterDepartmentId { get; set; }
    public string? FilterFromDate { get; set; }
    public string? FilterToDate { get; set; }
    public string? FilterPeriod { get; set; }
    public List<string> FilterPeriods { get; set; } = [];
    public List<Guid> FilterTagIds { get; set; } = [];
    public string? Sort { get; set; }
    public IReadOnlyList<UserSignupConflictItem> UserActiveSignups { get; set; } = [];
    public IReadOnlyDictionary<int, ShiftWindow> RotaWindowsByDayOffset { get; set; } = new Dictionary<int, ShiftWindow>();

    /// <summary>Controller the date-range Sign Up form posts to. Default = the
    /// public Shifts controller; the onboarding widget overrides this so the
    /// inline rota table posts back into the widget flow.</summary>
    public string SignUpRangeController { get; set; } = "Shifts";
    public string SignUpRangeAction { get; set; } = "SignUpRange";
}

public class EventRotaTableViewModel
{
    public List<ShiftDisplayItem> Shifts { get; set; } = [];
    public EventSettings EventSettings { get; set; } = null!;
    public HashSet<Guid> UserSignupShiftIds { get; set; } = [];
    public Dictionary<Guid, SignupStatus> UserSignupStatuses { get; set; } = new();
    public bool ShowSignups { get; set; }

    /// <summary>
    /// True when the viewer has no dietary preferences recorded; per-shift Sign-Up
    /// buttons are rendered disabled with the locked-out copy.
    /// </summary>
    public bool SignupsBlockedByMissingDietary { get; set; }

    public Guid? FilterDepartmentId { get; set; }
    public string? FilterFromDate { get; set; }
    public string? FilterToDate { get; set; }
    public string? FilterPeriod { get; set; }
    public List<string> FilterPeriods { get; set; } = [];
    public List<Guid> FilterTagIds { get; set; } = [];
    public string? Sort { get; set; }

    /// <summary>Controller the per-shift Sign Up form posts to. Default = the
    /// public Shifts controller; the onboarding widget overrides this so its
    /// inline rota table posts back into the widget flow.</summary>
    public string SignUpController { get; set; } = "Shifts";
    public string SignUpAction { get; set; } = "SignUp";
}

// === No-Show History ===

public class NoShowHistoryItem
{
    public string ShiftLabel { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public string ShiftDateLabel { get; set; } = string.Empty;
    public string? MarkedByName { get; set; }
    public string? MarkedAtLabel { get; set; }
}

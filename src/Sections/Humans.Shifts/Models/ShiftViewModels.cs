using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Humans.Shifts.Services;
using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Domain;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Shifts.Models;

/// <summary>
/// Shifts-owned knobs only. The app-wide calendar (name, dates, offsets, early entry) is Settings', edited at
/// <c>/Settings#event</c>.
/// </summary>
internal sealed class EventSettingsViewModel
{
    [BindRequired]
    public bool IsShiftBrowsingOpen { get; set; }
    [BindRequired]
    public int? GlobalVolunteerCap { get; set; }
    [BindRequired]
    public int ReminderLeadTimeHours { get; set; } = 24;
}

internal class CreateRotaModel
{
    [Required, MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    [BindRequired]
    public ShiftPriority Priority { get; set; }
    [BindRequired]
    public SignupPolicy Policy { get; set; }
    [BindRequired]
    public RotaPeriod Period { get; set; } = RotaPeriod.Event;

    [MaxLength(2000)]
    public string? PracticalInfo { get; set; }

    public string? TagIds { get; set; }
}

internal sealed class EditRotaModel : CreateRotaModel
{
    public Guid RotaId { get; set; }
}

internal sealed class MoveRotaModel
{
    public Guid TargetTeamId { get; set; }
}

internal class CreateShiftModel
{
    public Guid RotaId { get; set; }

    [MaxLength(2000)]
    public string? Description { get; set; }

    [BindRequired]
    public int DayOffset { get; set; }

    [Required]
    [BindRequired]
    public string StartTime { get; set; } = "08:00";

    [BindRequired]
    public double DurationHours { get; set; } = 4;

    [BindRequired]
    public int MinVolunteers { get; set; } = 1;
    [BindRequired]
    public int MaxVolunteers { get; set; } = 5;
    public bool AdminOnly { get; set; }
}

internal sealed class EditShiftModel : CreateShiftModel
{
    public Guid ShiftId { get; set; }
}

internal sealed class StaffingGridModel
{
    public Guid RotaId { get; set; }
    public List<DayStaffingEntry> Days { get; set; } = [];
}

internal sealed class DayStaffingEntry
{
    [BindRequired]
    public int DayOffset { get; set; }
    [BindRequired]
    public int MinVolunteers { get; set; } = 2;
    [BindRequired]
    public int MaxVolunteers { get; set; } = 5;
}

internal sealed class GenerateEventShiftsModel
{
    [BindRequired]
    public int StartDayOffset { get; set; }
    [BindRequired]
    public int EndDayOffset { get; set; }
    public List<TimeSlotEntry> TimeSlots { get; set; } = [];
    [BindRequired]
    public int MinVolunteers { get; set; } = 2;
    [BindRequired]
    public int MaxVolunteers { get; set; } = 5;
}

internal sealed class TimeSlotEntry
{
    [BindRequired]
    public string StartTime { get; set; } = "08:00";
    [BindRequired]
    public double DurationHours { get; set; } = 4;
}

internal sealed class ShiftBrowseViewModel
{
    public BurnSettingsInfo? EventSettings { get; set; }
    public List<DepartmentShiftGroup> Departments { get; set; } = [];
    public List<DepartmentOption> AllDepartments { get; set; } = [];
    public Guid? FilterDepartmentId { get; set; }
    public string? FilterFromDate { get; set; }
    public string? FilterToDate { get; set; }
    public string? FilterPeriod { get; set; }

    /// <summary>All three selected, or none set, means no period filtering.</summary>
    public List<string> FilterPeriods { get; set; } = [];

    /// <summary>ISO date; empty means all days.</summary>
    public string? FilterDay { get; set; }

    /// <summary>Independent of the current filters, so the option list stays stable as the viewer filters.</summary>
    public List<DayFilterOption> DayOptions { get; set; } = [];

    public bool ShowFullShifts { get; set; }
    public Guid UserId { get; set; }
    public HashSet<Guid> UserSignupShiftIds { get; set; } = [];
    public Dictionary<Guid, SignupStatus> UserSignupStatuses { get; set; } = new();
    public bool ShowSignups { get; set; }

    /// <summary>
    /// Early-entry close has passed for a non-privileged viewer. Rows combine it with
    /// <see cref="ShiftInfo.IsEarlyEntry"/> so only build shifts lock; mirrors the server gate in ShiftSignupService.
    /// </summary>
    public bool EarlyEntrySignupsClosed { get; set; }

    /// <summary>One per top-level department plus each promoted sub-team.</summary>
    public IReadOnlyList<DepartmentCoveragePie> CoveragePies { get; set; } = [];

    /// <summary>"urgency" for most-needed-first; empty groups by department.</summary>
    public string? Sort { get; set; }

    /// <summary>Populated only when <see cref="Sort"/> is "urgency".</summary>
    public List<RotaShiftGroup> UrgencyRankedRotas { get; set; } = [];

    public List<ShiftTagSummary> AllTags { get; set; } = [];

    public List<Guid> FilterTagIds { get; set; } = [];

    public HashSet<Guid> UserPreferredTagIds { get; set; } = [];

    public int MySignupCount { get; set; }

    /// <summary>Across all events, for the build/strike conflict-confirmation modal.</summary>
    public IReadOnlyList<UserSignupConflictItem> UserActiveSignups { get; set; } = [];
}

internal sealed class DepartmentOption
{
    public Guid TeamId { get; set; }
    public string Name { get; set; } = string.Empty;
}

internal sealed class DepartmentShiftGroup
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string? TeamDescription { get; set; }
    public string TeamSlug { get; set; } = string.Empty;
    public List<RotaShiftGroup> Rotas { get; set; } = [];
}

internal sealed class RotaShiftGroup
{
    public RotaInfo Rota { get; set; } = null!;
    public List<ShiftDisplayItem> Shifts { get; set; } = [];

    /// <summary>Set only in the urgency view, where rotas are shown flat.</summary>
    public string? DepartmentName { get; set; }

    public string? DepartmentSlug { get; set; }

    public double MaxUrgencyScore { get; set; }

    public int TotalConfirmed { get; set; }

    public int TotalSlots { get; set; }
}

/// <summary>
/// <see cref="SubPeriod"/> is set only for Build days inside one of the named sub-windows
/// (<see cref="BuildSubPeriodClassifier"/>).
/// </summary>
internal sealed record DayFilterOption(LocalDate Date, ShiftPeriod Period, BuildSubPeriod? SubPeriod);

internal sealed class ShiftDisplayItem
{
    public ShiftInfo Shift { get; set; } = null!;
    public Instant AbsoluteStart { get; set; }
    public Instant AbsoluteEnd { get; set; }
    public ShiftPeriod Period { get; set; }
    public int ConfirmedCount { get; set; }
    public int RemainingSlots { get; set; }
    public double UrgencyScore { get; set; }
    public IReadOnlyList<ShiftSignupInfo> Signups { get; set; } = [];
}

internal sealed class MyShiftsViewModel
{
    public BurnSettingsInfo? EventSettings { get; set; }
    public Guid UserId { get; set; }
    public List<MySignupItem> Upcoming { get; set; } = [];
    public List<MySignupItem> Pending { get; set; } = [];
    public List<MySignupItem> Past { get; set; } = [];
    public List<int> AvailableDayOffsets { get; set; } = [];
}

internal sealed class MySignupItem
{
    public ShiftSignup Signup { get; set; } = null!;
    public string? RotaName { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public Instant AbsoluteStart { get; set; }
    public Instant AbsoluteEnd { get; set; }

    /// <summary>
    /// Locks bail on an early-entry shift after close when the viewer cannot approve for its department, mirroring
    /// ShiftSignupService.BailAsync. Only the Mine action sets it.
    /// </summary>
    public bool BailLocked { get; set; }
}

internal sealed record ShiftAdminPendingSignup(
    Guid Id,
    Guid UserId,
    Guid? SignupBlockId,
    Instant CreatedAt,
    ShiftInfo Shift,
    int ShiftConfirmedCount,
    string RotaName);

internal sealed class ShiftAdminViewModel
{
    public TeamInfo Department { get; set; } = null!;
    public BurnSettingsInfo EventSettings { get; set; } = null!;
    public List<ShiftAdminRota> Rotas { get; set; } = [];
    public List<ShiftAdminPendingSignup> PendingSignups { get; set; } = [];
    public int TotalSlots { get; set; }
    public int ConfirmedCount { get; set; }
    public bool CanManageShifts { get; set; }
    public bool CanApproveSignups { get; set; }
    public Dictionary<Guid, VolunteerBadgesViewModel> VolunteerProfiles { get; set; } = new();

    /// <summary>Keyed by UserId for every signup on the page; views read this, never <c>ShiftSignup.User</c>.</summary>
    public IReadOnlyDictionary<Guid, UserInfo> Users { get; set; } = new Dictionary<Guid, UserInfo>();

    public bool CanViewMedical { get; set; }
    public List<DailyStaffingData> StaffingData { get; set; } = [];
    public List<DailyStaffingHours> StaffingHours { get; set; } = [];
    public Instant Now { get; set; }
    public List<DepartmentOption> AllDepartments { get; set; } = [];

    public List<ShiftTagSummary> AllTags { get; set; } = [];

    /// <summary>Shows only signups whose users are missing required Volunteer consents.</summary>
    public bool IncompleteOnboardingFilter { get; set; }
}

internal sealed class ShiftInfoViewModel
{
    public List<string> SelectedSkills { get; set; } = [];
    [StringLength(200, ErrorMessage = "Validation_MaxLength")]
    public string? SkillOtherText { get; set; }
    public List<string> SelectedQuirks { get; set; } = [];
    public string? TimePreference { get; set; } // Mutually exclusive; persisted as a quirk value.
    public List<string> SelectedLanguages { get; set; } = [];
    [StringLength(200, ErrorMessage = "Validation_MaxLength")]
    public string? LanguageOtherText { get; set; }

    /// <summary>Not posted back; repopulated on every render.</summary>
    public IReadOnlyList<ShiftTagSummary> AllShiftTags { get; set; } = [];

    public List<Guid> EditableShiftTagIds { get; set; } = [];

    internal static readonly string[] SkillOptions = ["Bartending", "First Aid", "Driving", "Sound", "Electrical", "Construction", "Cooking", "Art", "DJ", "Other"];
    internal static readonly string[] LanguageOptions = ["English", "Spanish", "German", "French", "Italian", "Portuguese", "Catalan", "Other"];

    internal static readonly string[] TimePreferenceOptions = ["Early Bird", "Night Owl", "All Day", "No Preference"];

    internal static readonly string[] ToggleQuirkOptions = ["Sober Shift", "Work In Shade", "Quiet Work", "Physical Work OK", "No Heights"];

    private static readonly string[] StoredSkillOptions = SkillOptions.Where(s => !string.Equals(s, "Other", StringComparison.Ordinal)).ToArray();
    private static readonly string[] StoredLanguageOptions = LanguageOptions.Where(l => !string.Equals(l, "Other", StringComparison.Ordinal)).ToArray();

    internal static readonly Dictionary<string, string> SkillEmoji = new(StringComparer.Ordinal)
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

    internal static readonly Dictionary<string, string> LanguageEmoji = new(StringComparer.Ordinal)
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

    internal static readonly Dictionary<string, string> TimePreferenceEmoji = new(StringComparer.Ordinal)
    {
        ["Early Bird"] = "\U0001f305",
        ["Night Owl"] = "\U0001f319",
        ["All Day"] = "\u2600\ufe0f",
        ["No Preference"] = "\U0001f937"
    };

    internal static ShiftInfoViewModel FromProfile(ShiftVolunteerProfileInfo? profile)
    {
        IReadOnlyList<string> quirks = profile?.Quirks ?? [];
        IReadOnlyList<string> skills = profile?.Skills ?? [];
        IReadOnlyList<string> languages = profile?.Languages ?? [];

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

    internal static string? ExtractTimePreference(IReadOnlyList<string> quirks)
        => quirks.FirstOrDefault(q => TimePreferenceOptions.Contains(q, StringComparer.Ordinal));

    internal static List<string> ExtractToggleQuirks(IReadOnlyList<string> quirks)
        => quirks.Where(q => !TimePreferenceOptions.Contains(q, StringComparer.Ordinal)).ToList();

    internal static List<string> MergeQuirks(string? timePreference, List<string> toggleQuirks)
    {
        var result = new List<string>(toggleQuirks);
        if (!string.IsNullOrEmpty(timePreference))
            result.Add(timePreference);
        return result;
    }

    internal static List<string> ExtractUnknownSkills(IReadOnlyList<string> skills)
        => skills
            .Where(s => !s.StartsWith("Other:", StringComparison.Ordinal) &&
                !StoredSkillOptions.Contains(s, StringComparer.Ordinal))
            .ToList();

    internal static List<string> ExtractUnknownLanguages(IReadOnlyList<string> languages)
        => languages
            .Where(l => !l.StartsWith("Other:", StringComparison.Ordinal) &&
                !StoredLanguageOptions.Contains(l, StringComparer.Ordinal))
            .ToList();

    internal static List<string> ExtractUnknownQuirks(IReadOnlyList<string> quirks)
        => quirks
            .Where(q => !TimePreferenceOptions.Contains(q, StringComparer.Ordinal) &&
                !ToggleQuirkOptions.Contains(q, StringComparer.Ordinal))
            .ToList();

    internal static List<string> MergeSkills(List<string>? selectedSkills, string? skillOtherText, IReadOnlyList<string>? existingSkills)
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

    internal static List<string> MergeLanguages(List<string>? selectedLanguages, string? languageOtherText, IReadOnlyList<string>? existingLanguages)
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

    internal static List<string> MergePersistedQuirks(
        string? timePreference,
        List<string>? selectedQuirks,
        IReadOnlyList<string>? existingQuirks)
    {
        var result = MergeQuirks(timePreference, selectedQuirks ?? []);
        result.AddRange(ExtractUnknownQuirks(existingQuirks ?? []));
        return result.Distinct(StringComparer.Ordinal).ToList();
    }
}

internal sealed class ShiftDashboardViewModel
{
    public List<UrgentShiftInfo> Shifts { get; set; } = [];
    public List<DepartmentOption> Departments { get; set; } = [];
    public Guid? SelectedDepartmentId { get; set; }
    public Guid? SelectedRotaId { get; set; }
    public string? SelectedStartDate { get; set; }
    public string? SelectedEndDate { get; set; }
    public LocalDate? FilterStartDate { get; set; }
    public LocalDate? FilterEndDate { get; set; }
    public ShiftPeriod? SelectedPeriod { get; set; }
    public BuildSubPeriod? SelectedSubPeriod { get; set; }
    public BurnSettingsInfo EventSettings { get; set; } = null!;
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

internal sealed record BuildDayCountdown(int DaysToBuild, LocalDate FirstBuildDay, int Weeks, int RemainderDays);

internal sealed class VolunteerSearchResult
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

/// <summary>
/// FormPost: range form plus per-shift POST (onboarding widget). InstantToggle: per-day AJAX toggle (/Shifts).
/// </summary>
internal enum ShiftSignupInteraction
{
    FormPost,
    InstantToggle
}

/// <summary>
/// Rows are the leaf <see cref="ShiftSignupSummary"/>: the component is public and cannot take the
/// section-internal row view (CS0051).
/// </summary>
internal sealed class ShiftSignupsViewModel
{
    public List<ShiftSignupsRow> Upcoming { get; set; } = [];
    public List<ShiftSignupsRow> Pending { get; set; } = [];
    public List<ShiftSignupsRow> Past { get; set; } = [];
    public BurnSettingsInfo? EventSettings { get; set; }
    public ShiftSignupsViewMode ViewMode { get; set; }
    public Guid UserId { get; set; }
    public string? DisplayName { get; set; }
}

internal sealed record ShiftSignupsRow(ShiftSignupSummary Signup, string DepartmentName);

internal sealed class RotaHeaderViewModel
{
    public RotaInfo Rota { get; set; } = null!;
    public bool ShowPreferenceStar { get; set; }
    public bool ShowPeriodBadge { get; set; } = true;
    public bool ShowTags { get; set; } = true;
    public bool ShowDescription { get; set; } = true;
    public bool ShowPracticalInfo { get; set; } = true;
    public string BadgeSpacingClass { get; set; } = "ms-1";
    public string DescriptionCssClass { get; set; } = "text-muted small";
    public string PracticalInfoCssClass { get; set; } = "text-muted small";
}

internal sealed record UserSignupConflictItem(
    LocalDate Date,
    string RotaName,
    Instant AbsoluteStart,
    Instant AbsoluteEnd,
    string DisplayStart,
    string DisplayEnd);

internal sealed record ShiftWindow(Instant AbsoluteStart, Instant AbsoluteEnd);

internal sealed class BuildStrikeRotaTableViewModel
{
    public RotaShiftGroup RotaGroup { get; set; } = null!;

    /// <summary>The cross-section DTO, so the EF entity never reaches a view model.</summary>
    public BurnSettingsInfo EventSettings { get; set; } = null!;
    public HashSet<Guid> UserSignupShiftIds { get; set; } = [];
    public Dictionary<Guid, SignupStatus> UserSignupStatuses { get; set; } = new();
    public bool ShowSignups { get; set; }

    /// <summary>Propagated to each build/strike row; see <see cref="ShiftBrowseViewModel"/>.</summary>
    public bool EarlyEntrySignupsClosed { get; set; }

    public Guid? FilterDepartmentId { get; set; }
    public string? FilterFromDate { get; set; }
    public string? FilterToDate { get; set; }
    public string? FilterPeriod { get; set; }
    public List<string> FilterPeriods { get; set; } = [];
    public List<Guid> FilterTagIds { get; set; } = [];
    public string? Sort { get; set; }
    public IReadOnlyList<UserSignupConflictItem> UserActiveSignups { get; set; } = [];
    public IReadOnlyDictionary<int, ShiftWindow> RotaWindowsByDayOffset { get; set; } = new Dictionary<int, ShiftWindow>();

    /// <summary>Defaults to the onboarding widget so its inline rota table posts back into the widget flow.</summary>
    public string SignUpRangeController { get; set; } = "OnboardingWidget";
    public string SignUpRangeAction { get; set; } = "SignUpRange";

    public ShiftSignupInteraction Interaction { get; set; } = ShiftSignupInteraction.FormPost;
}

internal sealed class EventRotaTableViewModel
{
    public List<ShiftDisplayItem> Shifts { get; set; } = [];

    /// <inheritdoc cref="BuildStrikeRotaTableViewModel.EventSettings" />
    public BurnSettingsInfo EventSettings { get; set; } = null!;
    public HashSet<Guid> UserSignupShiftIds { get; set; } = [];
    public Dictionary<Guid, SignupStatus> UserSignupStatuses { get; set; } = new();
    public bool ShowSignups { get; set; }

    /// <summary>Propagated to each event row; see <see cref="ShiftBrowseViewModel"/>.</summary>
    public bool EarlyEntrySignupsClosed { get; set; }

    public Guid? FilterDepartmentId { get; set; }
    public string? FilterFromDate { get; set; }
    public string? FilterToDate { get; set; }
    public string? FilterPeriod { get; set; }
    public List<string> FilterPeriods { get; set; } = [];
    public List<Guid> FilterTagIds { get; set; } = [];
    public string? Sort { get; set; }

    /// <summary>FormPost only, which only the onboarding widget uses, hence the default.</summary>
    public string SignUpController { get; set; } = "OnboardingWidget";
    public string SignUpAction { get; set; } = "SignUp";

    public ShiftSignupInteraction Interaction { get; set; } = ShiftSignupInteraction.FormPost;
}

/// <summary>One Build/Strike all-day row.</summary>
internal sealed class BuildStrikeRotaRowViewModel
{
    public ShiftDisplayItem Item { get; set; } = null!;

    /// <inheritdoc cref="BuildStrikeRotaTableViewModel.EventSettings" />
    public BurnSettingsInfo Es { get; set; } = null!;
    public bool IsSignedUp { get; set; }
    public SignupStatus? SignupStatus { get; set; }

    /// <summary>Combined with <see cref="ShiftInfo.IsEarlyEntry"/> to lock only build shifts.</summary>
    public bool EarlyEntrySignupsClosed { get; set; }
    public ShiftSignupInteraction Interaction { get; set; } = ShiftSignupInteraction.FormPost;
}

/// <summary>
/// Shared by the Event and Build/Strike rows so the JS contract (class, data-* and ARIA attributes) stays in one
/// place.
/// </summary>
internal sealed class ShiftToggleButtonModel
{
    public Guid ShiftId { get; set; }
    public bool IsSignedUp { get; set; }
    public SignupStatus? Status { get; set; }
    public bool IsFull { get; set; }

    /// <summary>Renders disabled; the server-side gate in ShiftSignupService still enforces it.</summary>
    public bool EarlyEntryClosed { get; set; }
}

/// <summary>One timed Event-shift row; FormPost reuses the per-row signup form.</summary>
internal sealed class EventRotaRowViewModel
{
    public ShiftDisplayItem Item { get; set; } = null!;

    /// <inheritdoc cref="BuildStrikeRotaTableViewModel.EventSettings" />
    public BurnSettingsInfo Es { get; set; } = null!;
    public bool IsSignedUp { get; set; }
    public SignupStatus? SignupStatus { get; set; }

    /// <summary>Combined with <see cref="ShiftInfo.IsEarlyEntry"/> to lock only build shifts.</summary>
    public bool EarlyEntrySignupsClosed { get; set; }
    public ShiftSignupInteraction Interaction { get; set; } = ShiftSignupInteraction.FormPost;

    // FormPost only, which only the onboarding widget uses, hence the default.
    public string SignUpController { get; set; } = "OnboardingWidget";
    public string SignUpAction { get; set; } = "SignUp";
    public Guid? FilterDepartmentId { get; set; }
    public string? FilterFromDate { get; set; }
    public string? FilterToDate { get; set; }
    public string? FilterPeriod { get; set; }
    public IReadOnlyList<string> FilterPeriods { get; set; } = [];
    public IReadOnlyList<Guid> FilterTagIds { get; set; } = [];
    public string? Sort { get; set; }
}

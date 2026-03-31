using System.ComponentModel.DataAnnotations;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models;

// === EventSettings ===

public class EventSettingsViewModel
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

    public string EarlyEntryCapacityJson { get; set; } = "{}";
    public string? BarriosEarlyEntryAllocationJson { get; set; }

    public string? EarlyEntryClose { get; set; }

    public bool IsShiftBrowsingOpen { get; set; }
    public int? GlobalVolunteerCap { get; set; }
    public int ReminderLeadTimeHours { get; set; } = 24;
    public bool IsActive { get; set; }
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
    public EventSettings EventSettings { get; set; } = null!;
    public List<DepartmentShiftGroup> Departments { get; set; } = [];
    public List<DepartmentOption> AllDepartments { get; set; } = [];
    public Guid? FilterDepartmentId { get; set; }
    public string? FilterFromDate { get; set; }
    public string? FilterToDate { get; set; }
    public string? FilterPeriod { get; set; }
    public bool ShowFullShifts { get; set; }
    public HashSet<Guid> UserSignupShiftIds { get; set; } = [];
    public Dictionary<Guid, SignupStatus> UserSignupStatuses { get; set; } = new();
    public bool ShowSignups { get; set; }

    /// <summary>
    /// All available tags for the filter UI.
    /// </summary>
    public List<Humans.Domain.Entities.ShiftTag> AllTags { get; set; } = [];

    /// <summary>
    /// Currently selected tag IDs for filtering.
    /// </summary>
    public List<Guid> FilterTagIds { get; set; } = [];

    /// <summary>
    /// Tag IDs the current volunteer has selected as preferences (for highlighting).
    /// </summary>
    public HashSet<Guid> UserPreferredTagIds { get; set; } = [];
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
    public string TeamSlug { get; set; } = string.Empty;
    public List<RotaShiftGroup> Rotas { get; set; } = [];
}

public class RotaShiftGroup
{
    public Rota Rota { get; set; } = null!;
    public List<ShiftDisplayItem> Shifts { get; set; } = [];
}

public record ShiftSignupInfo(Guid UserId, string DisplayName, SignupStatus Status, string? ProfilePictureUrl);

public class ShiftDisplayItem
{
    public Shift Shift { get; set; } = null!;
    public Instant AbsoluteStart { get; set; }
    public Instant AbsoluteEnd { get; set; }
    public ShiftPeriod Period { get; set; }
    public int ConfirmedCount { get; set; }
    public int RemainingSlots { get; set; }
    public IReadOnlyList<ShiftSignupInfo> Signups { get; set; } = [];
}

// === Mine ===

public class MyShiftsViewModel
{
    public EventSettings? EventSettings { get; set; }
    public List<MySignupItem> Upcoming { get; set; } = [];
    public List<MySignupItem> Pending { get; set; } = [];
    public List<MySignupItem> Past { get; set; } = [];
    public string? ICalUrl { get; set; }
    public List<int> AvailableDayOffsets { get; set; } = [];
}

public class MySignupItem
{
    public ShiftSignup Signup { get; set; } = null!;
    public string DepartmentName { get; set; } = string.Empty;
    public Instant AbsoluteStart { get; set; }
    public Instant AbsoluteEnd { get; set; }
}

// === ShiftAdmin ===

public class ShiftAdminViewModel
{
    public Team Department { get; set; } = null!;
    public EventSettings EventSettings { get; set; } = null!;
    public List<Rota> Rotas { get; set; } = [];
    public List<ShiftSignup> PendingSignups { get; set; } = [];
    public int TotalSlots { get; set; }
    public int ConfirmedCount { get; set; }
    public bool CanManageShifts { get; set; }
    public bool CanApproveSignups { get; set; }
    public Dictionary<Guid, VolunteerEventProfile> VolunteerProfiles { get; set; } = new();
    public bool CanViewMedical { get; set; }
    public List<DailyStaffingData> StaffingData { get; set; } = [];
    public Instant Now { get; set; }
    public List<DepartmentOption> AllDepartments { get; set; } = [];

    /// <summary>
    /// All available tags for the tag picker UI.
    /// </summary>
    public List<Humans.Domain.Entities.ShiftTag> AllTags { get; set; } = [];
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
    public static readonly string[] LanguageOptions = ["English", "Spanish", "German", "French", "Italian", "Portuguese", "Other"];

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

    /// <summary>Extract the time preference value from a flat quirks array.</summary>
    public static string? ExtractTimePreference(List<string> quirks)
        => quirks.FirstOrDefault(q => TimePreferenceOptions.Contains(q, StringComparer.Ordinal));

    /// <summary>Extract toggle quirks (excluding time preferences) from a flat quirks array.</summary>
    public static List<string> ExtractToggleQuirks(List<string> quirks)
        => quirks.Where(q => !TimePreferenceOptions.Contains(q, StringComparer.Ordinal)).ToList();

    /// <summary>Merge a time preference and toggle quirks back into a flat quirks array.</summary>
    public static List<string> MergeQuirks(string? timePreference, List<string> toggleQuirks)
    {
        var result = new List<string>(toggleQuirks ?? []);
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
    public string? SelectedDate { get; set; }
    public EventSettings EventSettings { get; set; } = null!;
    public List<DailyStaffingData> StaffingData { get; set; } = [];
}

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

// === No-Show History ===

public class NoShowHistoryItem
{
    public string ShiftLabel { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public string ShiftDateLabel { get; set; } = string.Empty;
    public string? MarkedByName { get; set; }
    public string? MarkedAtLabel { get; set; }
}

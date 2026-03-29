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
}

public class EditRotaModel : CreateRotaModel
{
    public Guid RotaId { get; set; }
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
    public List<string> SelectedQuirks { get; set; } = [];
    public List<string> SelectedAllergies { get; set; } = [];
    public List<string> SelectedIntolerances { get; set; } = [];
    public List<string> SelectedLanguages { get; set; } = [];
    public string? DietaryPreference { get; set; }

    [DataType(DataType.MultilineText)]
    [Display(Name = "Medical Conditions")]
    public string? MedicalConditions { get; set; }

    // Defined options from spec
    public static readonly string[] SkillOptions = ["Bartending", "First Aid", "Driving", "Sound", "Electrical", "Construction", "Cooking", "Art", "DJ", "Other"];
    public static readonly string[] QuirkOptions = ["Sober Shift", "Work In Shade", "Night Owl", "Early Bird", "Quiet Work", "Physical Work OK", "No Heights"];
    public string? AllergyOtherText { get; set; }
    public string? IntoleranceOtherText { get; set; }

    public static readonly string[] AllergyOptions = ["Celiac", "Shellfish", "Nuts", "Tree Nuts", "Soy", "Egg", "Other"];
    public static readonly string[] IntoleranceOptions = ["Gluten", "Peppers", "Shellfish", "Nuts", "Egg", "Lactose", "Other"];
    public static readonly string[] DietaryOptions = ["Omnivore", "Vegetarian", "Vegan", "Pescatarian"];
    public static readonly string[] LanguageOptions = ["English", "Spanish", "German", "French", "Italian", "Portuguese", "Other"];
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

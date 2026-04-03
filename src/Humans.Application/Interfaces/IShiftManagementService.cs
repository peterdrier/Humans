using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces;

/// <summary>
/// Consolidated service for shift management: authorization, event settings,
/// rotas, shifts, and urgency scoring.
/// </summary>
public interface IShiftManagementService
{
    // === Authorization ===

    /// <summary>
    /// Whether the user is a department coordinator for the given team
    /// (has a management role on a parent team).
    /// </summary>
    Task<bool> IsDeptCoordinatorAsync(Guid userId, Guid departmentTeamId);

    /// <summary>
    /// Whether the user can create/edit shifts and rotas for the department.
    /// True for dept coordinators, Admin, and VolunteerCoordinator (NOT NoInfoAdmin).
    /// </summary>
    Task<bool> CanManageShiftsAsync(Guid userId, Guid departmentTeamId);

    /// <summary>
    /// Whether the user can approve/refuse signups and voluntell for the department.
    /// True for dept coordinators, Admin, NoInfoAdmin, AND VolunteerCoordinator.
    /// </summary>
    Task<bool> CanApproveSignupsAsync(Guid userId, Guid departmentTeamId);

    /// <summary>
    /// Gets all team IDs (departments and sub-teams) where the user is a coordinator or manager.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetCoordinatorTeamIdsAsync(Guid userId);

    // === EventSettings ===

    /// <summary>
    /// Gets the single active EventSettings, or null if none.
    /// </summary>
    Task<EventSettings?> GetActiveAsync();

    /// <summary>
    /// Gets an EventSettings by primary key.
    /// </summary>
    Task<EventSettings?> GetByIdAsync(Guid id);

    /// <summary>
    /// Creates a new EventSettings. Validates only one IsActive=true.
    /// </summary>
    Task CreateAsync(EventSettings entity);

    /// <summary>
    /// Updates an existing EventSettings.
    /// </summary>
    Task UpdateAsync(EventSettings entity);

    /// <summary>
    /// Gets the available (non-barrios) EE slots for a given day offset.
    /// </summary>
    int GetAvailableEeSlots(EventSettings settings, int dayOffset);

    // === Rota ===

    /// <summary>
    /// Creates a new rota. Validates team is a department and event is active.
    /// </summary>
    Task CreateRotaAsync(Rota rota);

    /// <summary>
    /// Updates an existing rota.
    /// </summary>
    Task UpdateRotaAsync(Rota rota);

    /// <summary>
    /// Moves a rota to a different department (parent team).
    /// Preserves all shifts and signups. Records an audit log entry.
    /// </summary>
    Task MoveRotaToTeamAsync(Guid rotaId, Guid targetTeamId, Guid actorUserId, string actorDisplayName);

    /// <summary>
    /// Deletes a rota. Throws if child shifts have confirmed signups.
    /// </summary>
    Task DeleteRotaAsync(Guid rotaId);

    /// <summary>
    /// Gets a rota by primary key with shifts included.
    /// </summary>
    Task<Rota?> GetRotaByIdAsync(Guid rotaId);

    /// <summary>
    /// Gets all rotas for a department in an event.
    /// </summary>
    Task<IReadOnlyList<Rota>> GetRotasByDepartmentAsync(Guid teamId, Guid eventSettingsId);

    // === Bulk Shift Creation ===

    /// <summary>
    /// Creates one all-day shift per day for a Build or Strike rota.
    /// Throws if the rota has Period=Event.
    /// </summary>
    Task CreateBuildStrikeShiftsAsync(Guid rotaId, Dictionary<int, (int Min, int Max)> dailyStaffing);

    /// <summary>
    /// Generates shifts for an Event rota as Cartesian product of days × time slots.
    /// Throws if the rota has Period != Event.
    /// </summary>
    Task GenerateEventShiftsAsync(Guid rotaId, int startDayOffset, int endDayOffset,
        List<(LocalTime StartTime, double DurationHours)> timeSlots, int minVolunteers = 2, int maxVolunteers = 5);

    // === Shift ===

    /// <summary>
    /// Creates a new shift. Validates DayOffset range and volunteer counts.
    /// </summary>
    Task CreateShiftAsync(Shift shift);

    /// <summary>
    /// Updates an existing shift.
    /// </summary>
    Task UpdateShiftAsync(Shift shift);

    /// <summary>
    /// Deletes a shift. Throws if confirmed signups exist; cancels pending signups.
    /// </summary>
    Task DeleteShiftAsync(Guid shiftId);

    /// <summary>
    /// Gets a shift by primary key.
    /// </summary>
    Task<Shift?> GetShiftByIdAsync(Guid shiftId);

    /// <summary>
    /// Gets all shifts for a rota.
    /// </summary>
    Task<IReadOnlyList<Shift>> GetShiftsByRotaAsync(Guid rotaId);

    /// <summary>
    /// Resolves absolute times and period for a shift.
    /// </summary>
    (Instant Start, Instant End, ShiftPeriod Period) ResolveShiftTimes(Shift shift, EventSettings eventSettings);

    // === Urgency ===

    /// <summary>
    /// Gets shifts ranked by urgency score, with optional filtering.
    /// </summary>
    Task<IReadOnlyList<UrgentShift>> GetUrgentShiftsAsync(
        Guid eventSettingsId, int? limit = null,
        Guid? departmentId = null, LocalDate? date = null);

    /// <summary>
    /// Gets all active shifts for browse page, with optional filtering. Includes full shifts.
    /// </summary>
    Task<IReadOnlyList<UrgentShift>> GetBrowseShiftsAsync(
        Guid eventSettingsId, Guid? departmentId = null,
        LocalDate? fromDate = null, LocalDate? toDate = null,
        bool includeAdminOnly = false, bool includeSignups = false,
        bool includeHidden = false);

    /// <summary>
    /// Calculates the urgency score for a single shift.
    /// Factors in remaining slots, priority, duration, understaffing, and time proximity.
    /// </summary>
    double CalculateScore(Shift shift, int confirmedCount, EventSettings eventSettings);

    // === Staffing & Summary ===

    /// <summary>
    /// Gets per-day staffing data for all periods (set-up, event, strike).
    /// </summary>
    Task<IReadOnlyList<DailyStaffingData>> GetStaffingDataAsync(
        Guid eventSettingsId, Guid? departmentId = null);

    /// <summary>
    /// Gets shifts summary for a department. Returns null if no rotas.
    /// </summary>
    Task<ShiftsSummaryData?> GetShiftsSummaryAsync(
        Guid eventSettingsId, Guid departmentTeamId);

    /// <summary>
    /// Gets all parent teams that have active rotas in the given event.
    /// </summary>
    Task<IReadOnlyList<(Guid TeamId, string TeamName)>> GetDepartmentsWithRotasAsync(
        Guid eventSettingsId);

    // === Shift Tags ===

    /// <summary>
    /// Gets all shift tags, ordered by name.
    /// </summary>
    Task<IReadOnlyList<ShiftTag>> GetAllTagsAsync();

    /// <summary>
    /// Searches tags by name (case-insensitive prefix/contains).
    /// </summary>
    Task<IReadOnlyList<ShiftTag>> SearchTagsAsync(string query);

    /// <summary>
    /// Gets or creates a tag by name. Returns existing if name already exists (case-insensitive).
    /// </summary>
    Task<ShiftTag> GetOrCreateTagAsync(string name);

    /// <summary>
    /// Sets the tags for a rota, replacing any existing tags.
    /// </summary>
    Task SetRotaTagsAsync(Guid rotaId, IReadOnlyList<Guid> tagIds);

    /// <summary>
    /// Gets a volunteer's tag preferences.
    /// </summary>
    Task<IReadOnlyList<ShiftTag>> GetVolunteerTagPreferencesAsync(Guid userId);

    /// <summary>
    /// Sets a volunteer's tag preferences, replacing any existing ones.
    /// </summary>
    Task SetVolunteerTagPreferencesAsync(Guid userId, IReadOnlyList<Guid> tagIds);

    /// <summary>
    /// Gets the number of distinct pending shift signups per team for an event.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetPendingShiftSignupCountsByTeamAsync(
        Guid eventSettingsId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A shift with its computed urgency score and fill status.
/// </summary>
public record UrgentShift(
    Shift Shift,
    double UrgencyScore,
    int ConfirmedCount,
    int RemainingSlots,
    string DepartmentName,
    IReadOnlyList<(Guid UserId, string DisplayName, SignupStatus Status, bool HasProfilePicture)> Signups);

/// <summary>
/// Per-day staffing data for set-up/event/strike visualization.
/// </summary>
public record DailyStaffingData(
    int DayOffset,
    string DateLabel,
    int ConfirmedCount,
    int TotalSlots,
    int MinSlots,
    string Period);

/// <summary>
/// Aggregated shift summary for a department.
/// </summary>
public record ShiftsSummaryData(
    int TotalSlots,
    int ConfirmedCount,
    int PendingCount,
    int UniqueVolunteerCount);

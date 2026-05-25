using Humans.Application.DTOs;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Enums;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Shifts;

public record ShiftTagSummary(Guid Id, string Name);
public record ShiftTagPreferenceSummary(Guid Id, string Name);

/// <summary>
/// Consolidated service for shift management: authorization, event settings,
/// rotas, shifts, and urgency scoring.
/// </summary>
public interface IShiftManagementService : IApplicationService
{
    // === Authorization ===

    /// <summary>
    /// Whether the user is a department coordinator for the given team
    /// (has a management role on a parent team).
    /// </summary>
    Task<bool> IsDeptCoordinatorAsync(Guid userId, Guid departmentTeamId);

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
    /// Deletes an event and all Shifts-owned rows beneath it: rotas, shifts,
    /// and shift signups. Requires the current authenticated user to hold the
    /// full Admin role.
    /// </summary>
    Task<int> DeleteEventAsync(Guid eventSettingsId, CancellationToken cancellationToken = default);

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
    Task<RotaMoveResult> MoveRotaToTeamAsync(MoveRotaInput input);

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

    /// <summary>
    /// Volunteer-visible rotas in the active event whose <c>Name</c>
    /// contains <paramref name="query"/> (case-insensitive). The owning
    /// team's display name is stitched in via <c>ITeamService</c>
    /// (cross-domain — this service does not navigate the rota's team
    /// navigation property). Capped at <paramref name="max"/>; returned
    /// in unspecified order — the global search orchestrator scores and
    /// ranks. Returns an empty list when no event is active. Used by the
    /// global /Search page (<c>SearchService</c>); every caller sees the
    /// public surface regardless of role.
    /// </summary>
    Task<IReadOnlyList<RotaSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default);

    // === Bulk Shift Creation ===

    /// <summary>
    /// Creates one all-day shift per day for a Build or Strike rota.
    /// Throws if the rota has Period=Event.
    /// </summary>
    Task<ShiftGenerationResult> CreateBuildStrikeShiftsAsync(ConfigureBuildStrikeStaffingInput input);

    /// <summary>
    /// Generates shifts for an Event rota as Cartesian product of days × time slots.
    /// Throws if the rota has Period != Event.
    /// </summary>
    Task<ShiftGenerationResult> GenerateEventShiftsAsync(GenerateEventShiftsInput input);

    // === Shift ===

    /// <summary>
    /// Creates a new shift for a department rota. Validates rota ownership,
    /// period DayOffset range, and volunteer counts.
    /// </summary>
    Task<ShiftMutationResult> CreateShiftAsync(CreateShiftInput input);

    /// <summary>
    /// Updates an existing shift for a department rota. Validates shift
    /// ownership, period DayOffset range, and volunteer counts.
    /// </summary>
    Task<ShiftMutationResult> UpdateShiftAsync(UpdateShiftInput input);

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
        Guid? departmentId = null,
        LocalDate? startDate = null, LocalDate? endDate = null,
        ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null);

    /// <summary>
    /// Gets all active shifts for browse page, with optional filtering. Includes full shifts.
    /// When <paramref name="priorityOnly"/> is true, results are restricted to shifts whose
    /// rota is <see cref="ShiftPriority.Important"/> or <see cref="ShiftPriority.Essential"/>,
    /// or whose rota has any shift where confirmed-signup count is below
    /// <see cref="Shift.MinVolunteers"/> (i.e. understaffed).
    /// </summary>
    Task<IReadOnlyList<UrgentShift>> GetBrowseShiftsAsync(
        Guid eventSettingsId, Guid? departmentId = null,
        LocalDate? fromDate = null, LocalDate? toDate = null,
        bool includeAdminOnly = false, bool includeSignups = false,
        bool includeHidden = false, bool priorityOnly = false);

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
        Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null);

    /// <summary>
    /// Gets per-day staffing hours across all periods, grouped by shift priority.
    /// Hours = shift duration × MaxVolunteers. All-day shifts count as 8 hours per slot.
    /// </summary>
    Task<IReadOnlyList<DailyStaffingHours>> GetStaffingHoursAsync(
        Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null);

    /// <summary>
    /// Gets shifts summary aggregated across one or more teams. Returns null if no rotas.
    /// </summary>
    Task<ShiftsSummaryData?> GetShiftsSummaryAsync(
        Guid eventSettingsId, IReadOnlyCollection<Guid> teamIds);

    /// <summary>
    /// Gets all parent teams that have active rotas in the given event.
    /// </summary>
    Task<IReadOnlyList<(Guid TeamId, string TeamName)>> GetDepartmentsWithRotasAsync(
        Guid eventSettingsId);

    /// <summary>
    /// Returns the subset of <paramref name="teamIds"/> that have at least
    /// one rota with at least one shift in the given event. Used by team-page
    /// aggregation to surface "N sub-teams have shifts" without letting the
    /// Team Page section read <c>rotas</c> or <c>shifts</c> directly
    /// (design-rules §2c).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetTeamIdsWithShiftsInEventAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns one row per department pie shown above the /Shifts page.
    /// Pie-eligible teams = top-level departments + promoted sub-teams
    /// (<see cref="Team.IsInDirectory"/>). Non-promoted sub-team rotas roll
    /// up to their parent's pie. AdminOnly shifts and hidden rotas are
    /// excluded. Date filters are applied per-shift via
    /// <c>EventSettings.GateOpeningDate + DayOffset</c>.
    /// Rows are returned in natural <c>TeamName</c> order; the
    /// "promoted sub-team next to its parent" display ordering is applied
    /// in the view-model assembly layer.
    /// </summary>
    Task<IReadOnlyList<DepartmentCoveragePie>> GetDepartmentCoveragePiesAsync(
        Guid eventSettingsId,
        LocalDate? fromDate = null,
        LocalDate? toDate = null,
        CancellationToken ct = default);

    // === Coordinator Dashboard ===

    /// <summary>
    /// Gets the full coordinator-dashboard overview (counters + per-department staffing rows with subgroup drill-down).
    /// </summary>
    Task<DashboardOverview> GetDashboardOverviewAsync(Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null);

    /// <summary>
    /// Gets per-team coordinator activity, scoped to teams with at least one pending signup.
    /// When <paramref name="period"/> is non-null, only signups on shifts in that period count.
    /// </summary>
    Task<IReadOnlyList<CoordinatorActivityRow>> GetCoordinatorActivityAsync(Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null);

    /// <summary>
    /// Gets daily trend points (signups, ticket sales, distinct logins) for the window.
    /// Ticket sales and logins are unaffected by <paramref name="period"/>; the signups
    /// series is scoped to shifts in that period when non-null.
    /// </summary>
    Task<IReadOnlyList<DashboardTrendPoint>> GetDashboardTrendsAsync(
        Guid eventSettingsId, TrendWindow window, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null);

    /// <summary>
    /// Per-day stacked breakdown of Confirmed volunteers, grouped by parent
    /// department. Only returns data for <see cref="ShiftPeriod.Build"/> and
    /// <see cref="ShiftPeriod.Strike"/>; returns an empty list for Event or
    /// when <paramref name="period"/> is null. Subteam signups roll up into
    /// the parent department.
    /// </summary>
    Task<IReadOnlyList<DailyDepartmentStaffing>> GetDailyDepartmentStaffingAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null);

    /// <summary>
    /// Breakdown of shift counts by duration bucket for the given period.
    /// Full-day shifts are grouped into one bucket regardless of nominal hours;
    /// other shifts are bucketed by whole-hour duration. Returns empty when
    /// <paramref name="period"/> is null (the "All" view on the dashboard
    /// deliberately omits this breakdown).
    /// </summary>
    Task<IReadOnlyList<ShiftDurationBreakdownRow>> GetShiftDurationBreakdownAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null);

    /// <summary>
    /// Builds a rota × day coverage heatmap for the selected period (or the
    /// full event schedule when <paramref name="period"/> is null). Each cell
    /// reports slot fill on a single calendar day, based on shifts that
    /// overlap that day. Returns an empty heatmap if no visible shifts exist.
    /// </summary>
    Task<CoverageHeatmap> GetCoverageHeatmapAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null);

    /// <summary>
    /// Returns overall shift coverage for the active event:
    /// (filled signups / total slots, plus the ratio).
    /// Returns (0, 0, 0d) if no event is active.
    /// Used by the admin dashboard's shift-coverage stat tile.
    /// </summary>
    Task<(int Filled, int Total, double Ratio)> GetOverallCoverageAsync(CancellationToken ct = default);

    // === Shift Tags ===

    /// <summary>
    /// Gets shift tags, optionally filtered by name (case-insensitive contains).
    /// </summary>
    Task<IReadOnlyList<ShiftTagSummary>> GetTagsAsync(string? query = null);

    /// <summary>
    /// Gets or creates a tag by name. Returns existing if name already exists (case-insensitive).
    /// </summary>
    Task<ShiftTagSummary> GetOrCreateTagAsync(string name);

    /// <summary>
    /// Sets the tags for a rota, replacing any existing tags.
    /// </summary>
    Task SetRotaTagsAsync(Guid rotaId, IReadOnlyList<Guid> tagIds);

    /// <summary>
    /// Gets a volunteer's tag preferences.
    /// </summary>
    Task<IReadOnlyList<ShiftTagPreferenceSummary>> GetVolunteerTagPreferencesAsync(Guid userId);

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

    // ---- Methods moved from IProfileService (Profile-section migration §15 Step 0) ----
    // VolunteerEventProfile is owned by the Shifts section, not the Profile section.

    /// <summary>
    /// Gets or creates the user's shift profile (1:1 with User).
    /// </summary>
    Task<VolunteerEventProfile> GetOrCreateShiftProfileAsync(Guid userId);

    /// <summary>
    /// Updates a volunteer shift profile.
    /// </summary>
    Task UpdateShiftProfileAsync(VolunteerEventProfile profile);

    /// <summary>
    /// Gets a user's shift profile (Skills / Quirks / Languages). Dietary and
    /// medical data moved to Profile — read those via <c>IUserServiceRead</c>.
    /// </summary>
    Task<VolunteerEventProfile?> GetShiftProfileAsync(Guid userId);

    /// <summary>
    /// True when the user has at least one Pending or Confirmed signup on a
    /// future-or-current qualifying shift (see <see cref="Shift.QualifiesForCantinaMeal"/>).
    /// Used by the dashboard Things-to-do nudge for dietary/medical info.
    /// Returns false when no active event settings exist (fail closed).
    /// </summary>
    Task<bool> HasQualifyingCantinaSignupAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct user ids of volunteers on-site for the given event
    /// day — those with a Pending/Confirmed signup on a <see cref="Shift"/> whose
    /// <see cref="Shift.DayOffset"/> matches. Service-layer read for the Cantina
    /// roster (feature #36) so it never reaches into the Shifts repository.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetOnSiteUserIdsForDayAsync(
        int dayOffset,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes every <c>VolunteerEventProfile</c> row owned by
    /// <paramref name="userId"/>. Returns the number of rows removed. Used by
    /// the account anonymization flow so the job does not write to
    /// <c>volunteer_event_profiles</c> directly (design-rules §2c).
    /// </summary>
    Task<int> DeleteShiftProfilesForUserAsync(
        Guid userId, CancellationToken ct = default);
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
    IReadOnlyList<(Guid UserId, string DisplayName, SignupStatus Status)> Signups);

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

/// <summary>
/// One pie shown above the /Shifts page. Hours are decimal so callers can
/// render an exact percentage; the ratio <c>FilledHours / RequestedHours</c>
/// is the disc fill. <see cref="ParentTeamName"/> is non-null only for
/// promoted sub-team rows and carries the parent's display name so the
/// presentation layer can group sub-teams next to their parent without a
/// second team lookup.
/// </summary>
public record DepartmentCoveragePie(
    Guid TeamId,
    string TeamName,
    string TeamSlug,
    bool IsSubTeam,
    Guid? ParentTeamId,
    string? ParentTeamName,
    decimal RequestedHours,
    decimal FilledHours)
{
    /// <summary>
    /// Filled / requested as an integer 0..100. Single source of truth for
    /// the disc fill — service caps the inputs so the ratio is bounded, but
    /// we clamp here too in case a future contributor wires a different
    /// input path.
    /// </summary>
    public int FillPercent => RequestedHours > 0
        ? Math.Clamp(
            (int)Math.Round(FilledHours / RequestedHours * 100m, MidpointRounding.AwayFromZero),
            0, 100)
        : 0;
}

/// <summary>
/// Per-day staffing hours grouped by shift priority for volume visualization.
/// Hours = shift duration × MaxVolunteers. All-day shifts count as 8h per slot.
/// </summary>
public record DailyStaffingHours(
    int DayOffset,
    string DateLabel,
    double EssentialHours,
    double ImportantHours,
    double NormalHours);

public sealed record CreateShiftInput(
    Guid RotaId,
    Guid TeamId,
    string? Description,
    int DayOffset,
    LocalTime StartTime,
    double DurationHours,
    int MinVolunteers,
    int MaxVolunteers,
    bool AdminOnly,
    bool IsAllDay);

public sealed record UpdateShiftInput(
    Guid ShiftId,
    Guid TeamId,
    string? Description,
    int DayOffset,
    LocalTime StartTime,
    double DurationHours,
    int MinVolunteers,
    int MaxVolunteers,
    bool AdminOnly);

public sealed record GenerateEventShiftsInput(
    Guid RotaId,
    Guid TeamId,
    int StartDayOffset,
    int EndDayOffset,
    IReadOnlyList<ShiftTimeSlotInput> TimeSlots,
    int MinVolunteers,
    int MaxVolunteers);

public sealed record ShiftTimeSlotInput(LocalTime StartTime, double DurationHours);

public sealed record ConfigureBuildStrikeStaffingInput(
    Guid RotaId,
    Guid TeamId,
    IReadOnlyList<DayStaffingInput> Days);

public sealed record DayStaffingInput(int DayOffset, int MinVolunteers, int MaxVolunteers);

public sealed record MoveRotaInput(
    Guid RotaId,
    Guid SourceTeamId,
    Guid TargetTeamId,
    Guid ActorUserId);

public sealed record ShiftMutationResult(bool Succeeded, string Message, Guid? ShiftId = null)
{
    public static ShiftMutationResult Success(string message, Guid shiftId) => new(true, message, shiftId);
    public static ShiftMutationResult Failure(string message) => new(false, message);
}

public sealed record ShiftGenerationResult(bool Succeeded, string Message, int CreatedCount = 0)
{
    public static ShiftGenerationResult Success(string message, int createdCount) => new(true, message, createdCount);
    public static ShiftGenerationResult Failure(string message) => new(false, message);
}

public sealed record RotaMoveResult(bool Succeeded, string Message, string? RedirectSlug = null)
{
    public static RotaMoveResult Success(string message, string redirectSlug) => new(true, message, redirectSlug);
    public static RotaMoveResult Failure(string message) => new(false, message);
}

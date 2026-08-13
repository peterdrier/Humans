using Humans.Application.DTOs;
using Humans.Shifts.Services.Dtos;
using Humans.Application.Enums;
using Humans.Domain.Entities;
using Humans.Shifts.Domain;
using Humans.Domain.Enums;

using Humans.Shifts.Contracts;

using NodaTime;
using Humans.Application.Interfaces;

namespace Humans.Shifts.Services;


/// <summary>
/// Consolidated service for shift management: authorization, event settings,
/// rotas, shifts, and urgency scoring.
/// </summary>
/// <remarks>
/// The section's own interface. The members something outside the section
/// calls live on <see cref="IShiftManagementServiceRead"/>,
/// <see cref="IShiftVolunteerProfiles"/> and <see cref="IShiftSeeding"/> in
/// <c>Humans.Shifts.Contracts</c>; this inherits all three, so the section's
/// ~73 own call sites are unchanged. Everything declared here — rota and
/// shift CRUD, bulk generation, the coordinator dashboard's aggregate reads,
/// the coverage heatmap and the post-event stats — has no external caller.
/// </remarks>
internal interface IShiftManagementService
    : IShiftManagementServiceRead, IShiftVolunteerProfiles, IShiftSeeding, IApplicationService
{
    // === Event settings ===
    //
    // The entity-shaped reads and writes. IShiftSeeding carries the input-record
    // forms of the two creates for Humans.Development's fixture; these are the
    // section's own (nobodies-collective/Humans#866).

    /// <summary>
    /// Gets all rotas for a department in an event. Section-internal since the
    /// section's G5: its only outside caller was Shell's widget gallery, which
    /// moved in as ShiftsGalleryViewComponent (nobodies-collective/Humans#866).
    /// </summary>
    Task<IReadOnlyList<Rota>> GetRotasByDepartmentAsync(Guid teamId, Guid eventSettingsId);

    /// <summary>
    /// Gets or creates the user's shift profile (1:1 with User). Section-internal
    /// since the section's G5: its only outside caller was Shell's
    /// /Profile/Me/ShiftInfo POST, which moved onto ShiftProfileController.
    /// </summary>
    Task<VolunteerEventProfile> GetOrCreateShiftProfileAsync(Guid userId);

    /// <summary>
    /// Updates a volunteer shift profile. Section-internal for the same reason as
    /// <see cref="GetOrCreateShiftProfileAsync"/>.
    /// </summary>
    Task UpdateShiftProfileAsync(VolunteerEventProfile profile);

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
    /// Creates a new rota. Validates team is a department and event is active.
    /// </summary>
    Task CreateRotaAsync(Rota rota, IReadOnlyList<Guid>? tagIds = null);

    // === Authorization ===

    /// <summary>
    /// Whether the user can approve/refuse signups and voluntell for the department.
    /// True for dept coordinators, Admin, NoInfoAdmin, AND VolunteerCoordinator.
    /// </summary>
    Task<bool> CanApproveSignupsAsync(Guid userId, Guid departmentTeamId);

    // === Rota ===

    /// <summary>
    /// Updates an existing rota.
    /// </summary>
    Task UpdateRotaAsync(Rota rota, IReadOnlyList<Guid>? tagIds = null);

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

    // === Shift Summary by Camp ===

    /// <summary>
    /// Builds the Shift Summary view data for one scope within
    /// <paramref name="activeEvent"/>: a flat per-human table (one row per human
    /// with ≥1 confirmed signup in scope) and a by-camp pivot table (the active-
    /// camp roster for the current public year left-joined with the confirmed
    /// totals, so camps with nobody in scope appear as zero rows; humans with no
    /// active camp form the campless bucket), plus drill-down links.
    /// <para>
    /// Scope is implied by the arguments: no <paramref name="teamSlug"/> → global
    /// (all teams in the event); <paramref name="teamSlug"/> → that team's
    /// team-set (the team plus its non-promoted sub-teams);
    /// <paramref name="teamSlug"/> + <paramref name="rotaId"/> → a single rota.
    /// </para>
    /// Camp labels and roster come from <c>ICampServiceRead</c>; display names
    /// from <c>IUserServiceRead</c> — no new cross-section interface. Returns
    /// null when <paramref name="teamSlug"/> matches no team, or
    /// <paramref name="rotaId"/> does not belong to the team-set within the event
    /// (the controller maps null to 404). Rows are unsorted; the controller
    /// orders for display.
    /// </summary>
    Task<ShiftSummary?> BuildSummaryAsync(
        BurnSettingsInfo activeEvent,
        string? teamSlug = null,
        Guid? rotaId = null,
        CancellationToken ct = default);

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

    // === Staffing & Summary ===

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

    // === Shift Tags ===

    /// <summary>
    /// Gets or creates a tag by name. Returns existing if name already exists (case-insensitive).
    /// </summary>
    Task<ShiftTagSummary> GetOrCreateTagAsync(string name);

    // === Post-Event Stats ===

    /// <summary>
    /// Builds post-event statistics: completion and no-show rates for every shift
    /// in the event, aggregated globally and broken down by department and period.
    /// Returns null when <paramref name="eventSettingsId"/> resolves to no event.
    /// </summary>
    Task<PostEventStats?> GetPostEventStatsAsync(
        Guid eventSettingsId,
        CancellationToken ct = default);
}

/// <summary>
/// One pie shown above the /Shifts page. Hours are decimal so callers can
/// render an exact percentage; the ratio <c>FilledHours / RequestedHours</c>
/// is the disc fill. <see cref="ParentTeamName"/> is non-null only for
/// promoted sub-team rows and carries the parent's display name so the
/// presentation layer can group sub-teams next to their parent without a
/// second team lookup.
/// </summary>
internal sealed record DepartmentCoveragePie(
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

internal sealed record UpdateShiftInput(
    Guid ShiftId,
    Guid TeamId,
    string? Description,
    int DayOffset,
    LocalTime StartTime,
    double DurationHours,
    int MinVolunteers,
    int MaxVolunteers,
    bool AdminOnly);

internal sealed record GenerateEventShiftsInput(
    Guid RotaId,
    Guid TeamId,
    int StartDayOffset,
    int EndDayOffset,
    IReadOnlyList<ShiftTimeSlotInput> TimeSlots,
    int MinVolunteers,
    int MaxVolunteers);

internal sealed record ShiftTimeSlotInput(LocalTime StartTime, double DurationHours);

internal sealed record ConfigureBuildStrikeStaffingInput(
    Guid RotaId,
    Guid TeamId,
    IReadOnlyList<DayStaffingInput> Days);

internal sealed record DayStaffingInput(int DayOffset, int MinVolunteers, int MaxVolunteers);

internal sealed record MoveRotaInput(
    Guid RotaId,
    Guid SourceTeamId,
    Guid TargetTeamId,
    Guid ActorUserId);

internal sealed record ShiftGenerationResult(bool Succeeded, string Message, int CreatedCount = 0)
{
    internal static ShiftGenerationResult Success(string message, int createdCount) => new(true, message, createdCount);
    internal static ShiftGenerationResult Failure(string message) => new(false, message);
}

internal sealed record RotaMoveResult(bool Succeeded, string Message, string? RedirectSlug = null)
{
    internal static RotaMoveResult Success(string message, string redirectSlug) => new(true, message, redirectSlug);
    internal static RotaMoveResult Failure(string message) => new(false, message);
}

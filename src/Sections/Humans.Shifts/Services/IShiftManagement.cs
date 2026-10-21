using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Domain;
using Humans.Shifts.Contracts;

using NodaTime;
using Humans.Base.Interfaces;

using Humans.Shifts.Models;

namespace Humans.Shifts.Services;


/// <summary>
/// Consolidated service for shift management: authorization, event settings,
/// rotas, shifts, and urgency scoring.
/// </summary>
/// <remarks>
/// The section's own interface; the cross-section members live on
/// <see cref="IShiftService"/>, <see cref="IShiftVolunteerProfiles"/>
/// and <see cref="IShiftSeeding"/>, which this inherits.
/// </remarks>
internal interface IShiftManagement
    : IShiftService, IShiftVolunteerProfiles, IShiftSeeding, IApplicationService
{
    // === Dashboard / staffing reads (section-internal — no caller outside the section) ===

    /// <summary>
    /// Gets the per-day staffing chart snapshot for all periods.
    /// </summary>
    Task<ShiftStaffingSnapshot> GetStaffingSnapshotAsync(
        Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null, CancellationToken ct = default);

    /// <summary>
    /// Gets all parent teams that have active rotas in the given event.
    /// </summary>
    Task<IReadOnlyList<(Guid TeamId, string TeamName)>> GetDepartmentsWithRotasAsync(
        Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>
    /// Returns overall shift coverage for the active event:
    /// (filled signups / total slots, plus the ratio).
    /// Returns (0, 0, 0d) if no event is active.
    /// Used by the admin dashboard's shift-coverage stat tile.
    /// </summary>
    Task<(int Filled, int Total, double Ratio)> GetOverallCoverageAsync(CancellationToken ct = default);

    // === Event settings ===

    /// <summary>
    /// Gets all rotas for a department in an event. Section-internal — no
    /// caller outside the section.
    /// </summary>
    Task<IReadOnlyList<ShiftAdminRota>> GetRotasByDepartmentAsync(Guid teamId, Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>
    /// Saves the user's shift-profile skills, quirks and languages, creating the
    /// profile row (1:1 with User) when it does not exist yet. Section-internal —
    /// no caller outside the section.
    /// </summary>
    Task UpdateShiftProfileAsync(ShiftVolunteerProfileInfo profile);

    /// <summary>
    /// The Shifts-owned knobs for an event, or null if a rota or knob edit
    /// hasn't created the row yet. "Active event" is Settings' concept
    /// (nobodies-collective/Humans#1631) — this section no longer tracks one.
    /// </summary>
    Task<ShiftEventKnobs?> GetKnobsAsync(Guid eventSettingsId);

    /// <summary>
    /// Creates or updates the knobs row for <paramref name="eventSettingsId"/>
    /// (on-demand insert — the row may not exist yet).
    /// </summary>
    Task SaveKnobsAsync(
        Guid eventSettingsId, bool isShiftBrowsingOpen, int? globalVolunteerCap, int reminderLeadTimeHours);

    /// <summary>
    /// Creates a new rota. Validates team is a department and event is active.
    /// </summary>
    Task CreateRotaAsync(Rota rota, IReadOnlyList<Guid>? tagIds = null);

    // === Authorization ===

    /// <summary>
    /// Whether the user can approve/refuse signups and voluntell for the department.
    /// True for dept coordinators, Admin, NoInfoAdmin, AND VolunteerCoordinator.
    /// </summary>
    Task<bool> CanApproveSignupsAsync(Guid userId, Guid departmentTeamId, CancellationToken ct = default);

    // === Rota ===

    /// <summary>
    /// Updates an existing rota's header fields, and its tags when
    /// <see cref="UpdateRotaInput.TagIds"/> is non-null. Returns false when the rota does not exist.
    /// </summary>
    Task<bool> UpdateRotaAsync(UpdateRotaInput input);

    /// <summary>
    /// Moves a rota to a different department (parent team).
    /// Preserves all shifts and signups. Records an audit log entry.
    /// </summary>
    Task<RotaMoveResult> MoveRotaToTeamAsync(MoveRotaInput input);

    /// <summary>
    /// Deletes a rota; returns a resource-key refusal for missing rotas or confirmed signups.
    /// </summary>
    Task<ShiftDeletionResult> DeleteRotaAsync(Guid rotaId, Guid actorUserId);

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
    /// Camp labels and roster come from <c>ICampService</c>; display names
    /// from <c>IUserService</c> — no new cross-section interface. Returns
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
    /// Deletes a shift and pending signups; returns a resource-key refusal for missing shifts or confirmed signups.
    /// </summary>
    Task<ShiftDeletionResult> DeleteShiftAsync(Guid shiftId, Guid actorUserId);

    /// <summary>
    /// Gets a shift and its rota's header fields by primary key; null only when the shift does not exist.
    /// </summary>
    Task<ShiftWithRota?> GetShiftByIdAsync(Guid shiftId, CancellationToken ct = default);

    // === Staffing & Summary ===

    /// <summary>
    /// Returns one row per department pie shown above the /Shifts page.
    /// Pie-eligible teams = top-level departments + promoted sub-teams
    /// (<see cref="ShiftManagementService.IsInDirectory"/>). Non-promoted sub-team rotas roll
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

    /// <summary>Evicts all cached coordinator-dashboard aggregates for an event after signup mutations.</summary>
    void InvalidateDashboardCaches(Guid eventSettingsId);

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

/// <summary>Edit of a rota's header fields; null <see cref="TagIds"/> leaves tags alone.</summary>
internal sealed record UpdateRotaInput(
    Guid RotaId,
    string Name,
    string? Description,
    string? PracticalInfo,
    ShiftPriority Priority,
    SignupPolicy Policy,
    RotaPeriod Period,
    bool IsVisibleToVolunteers,
    IReadOnlyList<Guid>? TagIds);

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

internal sealed record ShiftDeletionResult(string? ErrorKey = null, int ConfirmedSignups = 0);

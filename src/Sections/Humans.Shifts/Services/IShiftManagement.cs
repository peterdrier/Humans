using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Domain;
using Humans.Shifts.Contracts;

using NodaTime;
using Humans.Base.Interfaces;

using Humans.Shifts.Models;

namespace Humans.Shifts.Services;


/// <summary>
/// The section's own surface; the cross-section members are on <see cref="IShiftService"/>,
/// <see cref="IShiftVolunteerProfiles"/> and <see cref="IShiftSeeding"/>, which this inherits.
/// </summary>
internal interface IShiftManagement
    : IShiftService, IShiftVolunteerProfiles, IShiftSeeding, IApplicationService
{
    Task<ShiftStaffingSnapshot> GetStaffingSnapshotAsync(
        Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null, CancellationToken ct = default);

    /// <summary>Parent teams with active rotas in the event.</summary>
    Task<IReadOnlyList<(Guid TeamId, string TeamName)>> GetDepartmentsWithRotasAsync(
        Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>For the active event; (0, 0, 0d) when none is active.</summary>
    Task<(int Filled, int Total, double Ratio)> GetOverallCoverageAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ShiftAdminRota>> GetRotasByDepartmentAsync(Guid teamId, Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>Creates the profile row when it doesn't exist yet.</summary>
    Task UpdateShiftProfileAsync(ShiftVolunteerProfileInfo profile);

    /// <summary>Null until a rota or knob edit creates the row. The active event is Settings' concept.</summary>
    Task<ShiftEventKnobs?> GetKnobsAsync(Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>Inserts the row on demand.</summary>
    Task SaveKnobsAsync(
        Guid eventSettingsId, bool isShiftBrowsingOpen, int? globalVolunteerCap, int reminderLeadTimeHours);

    /// <summary>Validates that the team is a department and the event is active.</summary>
    Task CreateRotaAsync(Rota rota, IReadOnlyList<Guid>? tagIds = null);

    /// <summary>Dept coordinators, Admin, NoInfoAdmin and VolunteerCoordinator.</summary>
    Task<bool> CanApproveSignupsAsync(Guid userId, Guid departmentTeamId, CancellationToken ct = default);

    /// <summary>Tags change only when <see cref="UpdateRotaInput.TagIds"/> is non-null. False when the rota doesn't exist.</summary>
    Task<bool> UpdateRotaAsync(UpdateRotaInput input);

    /// <summary>Keeps all shifts and signups. Audited.</summary>
    Task<RotaMoveResult> MoveRotaToTeamAsync(MoveRotaInput input);

    /// <summary>Returns a resource-key refusal for a missing rota or one with confirmed signups.</summary>
    Task<ShiftDeletionResult> DeleteRotaAsync(Guid rotaId, Guid actorUserId);

    /// <summary>
    /// A per-human table (one row per human with a confirmed signup in scope) and a by-camp pivot (the active-camp roster
    /// left-joined with the totals, so empty camps show as zero; humans with no camp form the campless bucket). Scope:
    /// no <paramref name="teamSlug"/> is global; a slug is that team plus its non-promoted sub-teams; a slug and
    /// <paramref name="rotaId"/> is one rota. Null when the slug matches no team or the rota isn't in that team-set.
    /// Rows are unsorted.
    /// </summary>
    Task<ShiftSummary?> BuildSummaryAsync(
        BurnSettingsInfo activeEvent,
        string? teamSlug = null,
        Guid? rotaId = null,
        CancellationToken ct = default);

    /// <summary>One all-day shift per day. Throws for an Event rota.</summary>
    Task<ShiftGenerationResult> CreateBuildStrikeShiftsAsync(ConfigureBuildStrikeStaffingInput input);

    /// <summary>Days × time slots. Throws unless the rota is an Event rota.</summary>
    Task<ShiftGenerationResult> GenerateEventShiftsAsync(GenerateEventShiftsInput input);

    /// <summary>Validates ownership, the period's DayOffset range and volunteer counts.</summary>
    Task<ShiftMutationResult> UpdateShiftAsync(UpdateShiftInput input);

    /// <summary>Also deletes pending signups. Returns a resource-key refusal for a missing shift or one with confirmed signups.</summary>
    Task<ShiftDeletionResult> DeleteShiftAsync(Guid shiftId, Guid actorUserId);

    /// <summary>Null only when the shift doesn't exist.</summary>
    Task<ShiftWithRota?> GetShiftByIdAsync(Guid shiftId, CancellationToken ct = default);

    /// <summary>
    /// One pie per top-level department or promoted sub-team; other sub-teams roll up to their parent. AdminOnly shifts and
    /// hidden rotas are excluded. Rows come in <c>TeamName</c> order; the view model places promoted sub-teams by their
    /// parent.
    /// </summary>
    Task<IReadOnlyList<DepartmentCoveragePie>> GetDepartmentCoveragePiesAsync(
        Guid eventSettingsId,
        LocalDate? fromDate = null,
        LocalDate? toDate = null,
        CancellationToken ct = default);

    Task<DashboardOverview> GetDashboardOverviewAsync(Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null);

    /// <summary>
    /// Teams with at least one pending signup. A non-null <paramref name="period"/> counts only that period's signups.
    /// </summary>
    Task<IReadOnlyList<CoordinatorActivityRow>> GetCoordinatorActivityAsync(Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null);

    /// <summary>Only the signups series is scoped to <paramref name="period"/>; ticket sales and logins aren't.</summary>
    Task<IReadOnlyList<DashboardTrendPoint>> GetDashboardTrendsAsync(
        Guid eventSettingsId, TrendWindow window, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null);

    /// <summary>Call after signup mutations.</summary>
    void InvalidateDashboardCaches(Guid eventSettingsId);

    /// <summary>
    /// Confirmed volunteers by parent department. Build and Strike only: empty for Event or a null
    /// <paramref name="period"/>.
    /// </summary>
    Task<IReadOnlyList<DailyDepartmentStaffing>> GetDailyDepartmentStaffingAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null);

    /// <summary>
    /// Full-day shifts share one bucket; others bucket by whole hours. Empty when <paramref name="period"/> is null: the
    /// dashboard's "All" view omits it.
    /// </summary>
    Task<IReadOnlyList<ShiftDurationBreakdownRow>> GetShiftDurationBreakdownAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null);

    /// <summary>Each cell is slot fill on one calendar day, from the shifts overlapping it.</summary>
    Task<CoverageHeatmap> GetCoverageHeatmapAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null);

    /// <summary>Matches existing names case-insensitively.</summary>
    Task<ShiftTagSummary> GetOrCreateTagAsync(string name);

    /// <summary>Null when <paramref name="eventSettingsId"/> resolves to no event.</summary>
    Task<PostEventStats?> GetPostEventStatsAsync(
        Guid eventSettingsId,
        CancellationToken ct = default);
}

/// <summary>
/// <see cref="ParentTeamName"/> is set only for promoted sub-teams, so the view can group them by their parent without a
/// second lookup.
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
    /// <summary>Clamped here as well as by the service, so the disc fill is always 0..100.</summary>
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

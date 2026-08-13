using Humans.Domain.Entities;
using Humans.Domain.Enums;

using NodaTime;

namespace Humans.Shifts.Contracts;

/// <summary>
/// Cross-section read surface for the Shifts section. Carved from the call
/// sites, not from the interface: these are the members of the section's
/// internal <c>IShiftManagementService</c> that something outside the section
/// actually calls. The rota/shift generation surface, the coordinator
/// dashboard's twelve aggregate reads and the post-event stats have no
/// external caller and stay internal.
/// </summary>
/// <remarks>
/// See <c>memory/architecture/section-read-write-split.md</c>. The volunteer
/// shift-profile cluster is <see cref="IShiftVolunteerProfiles"/> (it carries
/// writes and would make this name a lie); the dev-fixture verbs are
/// <see cref="IShiftSeeding"/>.
/// </remarks>
public interface IShiftManagementServiceRead
{
    /// <summary>
    /// Whether the user is a department coordinator for the given team
    /// (has a management role on a parent team).
    /// </summary>
    Task<bool> IsDeptCoordinatorAsync(Guid userId, Guid departmentTeamId);

    /// <summary>
    /// Gets all team IDs (departments and sub-teams) where the user is a coordinator or manager.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetCoordinatorTeamIdsAsync(Guid userId);

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

    /// <summary>
    /// Gets shifts ranked by urgency score, with optional filtering.
    /// </summary>
    Task<IReadOnlyList<UrgentShiftInfo>> GetUrgentShiftsAsync(
        Guid eventSettingsId, int? limit = null,
        Guid? departmentId = null,
        LocalDate? startDate = null, LocalDate? endDate = null,
        ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null);

    /// <summary>
    /// Gets all active shifts for browse page, with optional filtering. Includes full shifts.
    /// When the query's <see cref="ShiftBrowseQueryFlags.PriorityOnly"/> flag is set, results are
    /// restricted to shifts whose
    /// rota is <see cref="ShiftPriority.Important"/> or <see cref="ShiftPriority.Essential"/>,
    /// or whose rota has any shift where confirmed-signup count is below
    /// <see cref="ShiftInfo.MinVolunteers"/> (i.e. understaffed).
    /// </summary>
    Task<IReadOnlyList<UrgentShiftInfo>> GetBrowseShiftsAsync(ShiftBrowseQuery query);

    /// <summary>
    /// Gets the per-day staffing chart snapshot for all periods.
    /// </summary>
    Task<ShiftStaffingSnapshot> GetStaffingSnapshotAsync(
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
    /// Returns overall shift coverage for the active event:
    /// (filled signups / total slots, plus the ratio).
    /// Returns (0, 0, 0d) if no event is active.
    /// Used by the admin dashboard's shift-coverage stat tile.
    /// </summary>
    Task<(int Filled, int Total, double Ratio)> GetOverallCoverageAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the number of distinct pending shift signups per team for the active event.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetActivePendingShiftSignupCountsByTeamAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the user has at least one Pending or Confirmed signup on a
    /// future-or-current qualifying shift (see the shift qualifying-duration rule).
    /// Used by the dashboard Things-to-do nudge for dietary/medical info.
    /// Returns false when no active event settings exist (fail closed).
    /// </summary>
    Task<bool> HasQualifyingCantinaSignupAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct user ids of volunteers on-site for the given event
    /// day — those with a Confirmed signup on a shift whose
    /// day offset matches. Service-layer read for the Cantina
    /// roster (feature #36) so it never reaches into the Shifts repository.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetOnSiteUserIdsForDayAsync(
        Guid eventSettingsId,
        int dayOffset,
        CancellationToken ct = default);
}

/// <summary>
/// One rota that matched a global-search query. Returned by
/// <see cref="IShiftManagementServiceRead.SearchAsync"/> with the owning team's
/// name already stitched so the orchestrator never has to call
/// <c>ITeamService</c> just to render the subtitle. Names-only matching:
/// only <see cref="Name"/> is matched.
/// </summary>
/// <param name="Name">Rota display name (the only matched field).</param>
/// <param name="TeamId">Owning team id; drives the rota detail URL.</param>
/// <param name="TeamName">Owning team display name; surfaced as subtitle.</param>
public record RotaSearchHit(
    string Name,
    Guid TeamId,
    string TeamName);

[Flags]
public enum ShiftBrowseQueryFlags
{
    None = 0,
    IncludeAdminOnly = 1,
    IncludeSignups = 2,
    IncludeHidden = 4,
    PriorityOnly = 8
}

public sealed record ShiftBrowseQuery(
    Guid EventSettingsId,
    Guid? DepartmentId = null,
    LocalDate? FromDate = null,
    LocalDate? ToDate = null,
    ShiftBrowseQueryFlags Flags = ShiftBrowseQueryFlags.None);

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
/// Per-day staffing hours grouped by shift priority for volume visualization.
/// Hours = shift duration × MaxVolunteers. All-day shifts count as 8h per slot.
/// </summary>
public record DailyStaffingHours(
    int DayOffset,
    string DateLabel,
    double EssentialHours,
    double ImportantHours,
    double NormalHours);

public sealed record ShiftStaffingSnapshot(
    IReadOnlyList<DailyStaffingData> StaffingData,
    IReadOnlyList<DailyStaffingHours> StaffingHours)
{
    public static ShiftStaffingSnapshot Empty { get; } = new([], []);
}

/// <summary>
/// Aggregated shift summary for a department.
/// </summary>
public record ShiftsSummaryData(
    int TotalSlots,
    int ConfirmedCount,
    int PendingCount,
    int UniqueVolunteerCount,
    IReadOnlySet<Guid> TeamIdsWithShifts);

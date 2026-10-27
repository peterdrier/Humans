
using NodaTime;

using Humans.Base.Interfaces;

namespace Humans.Shifts.Contracts;

/// <summary>
/// The members of the internal <c>IShiftManagement</c> that other sections call. Volunteer shift profiles are on
/// <see cref="IShiftVolunteerProfiles"/> (they carry writes); dev-fixture verbs on <see cref="IShiftSeeding"/>.
/// </summary>
public interface IShiftService : IApplicationService
{
    /// <summary>Holds a management role on the team's parent.</summary>
    Task<bool> IsDeptCoordinatorAsync(Guid userId, Guid departmentTeamId, CancellationToken ct = default);

    /// <summary>Departments and sub-teams the user coordinates or manages.</summary>
    Task<IReadOnlyList<Guid>> GetCoordinatorTeamIdsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Volunteer-visible rotas in the active event, matched by name and scored, in no order: the search
    /// orchestrator ranks. Every caller sees the public surface whatever their role.
    /// </summary>
    Task<IReadOnlyList<RotaSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// No visibility filter: the id came from a filtered search and the destination page enforces access.
    /// </summary>
    Task<RotaInfo?> GetRotaAsync(Guid rotaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Excludes admin-only shifts and hidden rotas unless asked; the role-protected dashboard opts in.
    /// </summary>
    Task<IReadOnlyList<UrgentShiftInfo>> GetUrgentShiftsAsync(
        Guid eventSettingsId, int? limit = null,
        Guid? departmentId = null,
        LocalDate? startDate = null, LocalDate? endDate = null,
        ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null,
        bool includeRestricted = false, CancellationToken ct = default);

    /// <summary>Includes full shifts.</summary>
    Task<IReadOnlyList<UrgentShiftInfo>> GetBrowseShiftsAsync(ShiftBrowseQuery query, CancellationToken ct = default);

    /// <summary>Null when the teams have no rotas.</summary>
    Task<ShiftsSummaryData?> GetShiftsSummaryAsync(
        Guid eventSettingsId, IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default);

    /// <summary>Distinct pending signups per team, in the active event.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetActivePendingShiftSignupCountsByTeamAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A Pending or Confirmed signup on a current or future shift long enough for cantina meals. Fails closed with
    /// no active event.
    /// </summary>
    Task<bool> HasQualifyingCantinaSignupAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>Users with a Confirmed signup on a shift that day: the Cantina roster.</summary>
    Task<IReadOnlyList<Guid>> GetOnSiteUserIdsForDayAsync(
        Guid eventSettingsId,
        int dayOffset,
        CancellationToken ct = default);
}

/// <summary>
/// Matched on <see cref="Name"/> only. <see cref="Name"/> is an alphabetical tiebreak for the orchestrator, never
/// rendered: <c>&lt;vc:shifts-search-result&gt;</c> fetches the row's display itself from <see cref="RotaId"/>.
/// </summary>
public record RotaSearchHit(
    Guid RotaId,
    string Name,
    int Score);

[Flags]
public enum ShiftBrowseQueryFlags
{
    None = 0,
    IncludeAdminOnly = 1,
    IncludeSignups = 2,
    IncludeHidden = 4
}

public sealed record ShiftBrowseQuery(
    Guid EventSettingsId,
    Guid? DepartmentId = null,
    LocalDate? FromDate = null,
    LocalDate? ToDate = null,
    ShiftBrowseQueryFlags Flags = ShiftBrowseQueryFlags.None);

public record DailyStaffingData(
    int DayOffset,
    string DateLabel,
    int ConfirmedCount,
    int TotalSlots,
    int MinSlots,
    string Period);

/// <summary>
/// Hours are each shift's overlap with the day times its MaxVolunteers; an all-day shift counts its 10h window.
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

public record ShiftsSummaryData(
    int TotalSlots,
    int ConfirmedCount,
    int PendingCount,
    int UniqueVolunteerCount,
    IReadOnlySet<Guid> TeamIdsWithShifts);

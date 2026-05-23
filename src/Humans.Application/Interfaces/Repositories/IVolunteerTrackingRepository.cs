using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Domain.Attributes;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Shifts-owned I/O for the new volunteer_build_statuses table plus the
/// scoped Build-period signup read used by the gap detector. All methods
/// return materialized lists / nullable rows — no IQueryable leaks.
/// </summary>
[Section("Shifts")]
public interface IVolunteerTrackingRepository : IRepository
{
    /// <summary>Fetch the row for (userId, eventSettingsId), or null.</summary>
    Task<VolunteerBuildStatus?> GetAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>All rows for the event keyed by UserId. Empty list if none.</summary>
    Task<IReadOnlyList<VolunteerBuildStatus>> GetByEventAsync(
        Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>
    /// Returns <see cref="VolunteerBuildStatus"/> rows for the supplied user
    /// ids in the given event, in one query. Read-only. Backs the bulk path
    /// on <see cref="Application.Services.Shifts.ShiftViewService.GetUsersAsync"/>.
    /// </summary>
    Task<IReadOnlyList<VolunteerBuildStatus>> GetByUsersAndEventAsync(
        IReadOnlyCollection<Guid> userIds, Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>
    /// Upsert (UserId, EventSettingsId): mutate or insert the row's camp set-up
    /// fields and, in the same save, optionally trim day-off entries that the
    /// new camp-setup span newly covers.
    /// <para>
    /// The caller has already validated <paramref name="barrioSetupStartDate"/>
    /// (or null to clear). When <paramref name="setupOffsetThreshold"/> is set,
    /// any <c>DayOffs</c> entries whose <c>DayOffset &gt;= threshold</c> are
    /// removed; pass null to skip the trim.
    /// </para>
    /// <para>Returns the offsets that were trimmed, sorted ascending. Empty
    /// list if no trim happened or no entries matched.</para>
    /// </summary>
    Task<IReadOnlyList<int>> UpsertCampSetupAsync(
        Guid userId,
        Guid eventSettingsId,
        LocalDate? barrioSetupStartDate,
        string? notes,
        Guid? setByUserId,
        Instant? setAt,
        int? setupOffsetThreshold,
        CancellationToken ct = default);

    /// <summary>
    /// Insert or replace a single day-off entry on the row's <c>DayOffs</c>
    /// collection. Creates the row if absent. Replaces any existing entry for
    /// the same <c>DayOffset</c> so there is at most one entry per day.
    /// Persists with the list sorted by <c>DayOffset</c> ascending.
    /// </summary>
    Task UpsertDayOffAsync(
        Guid userId,
        Guid eventSettingsId,
        DayOffEntry entry,
        CancellationToken ct = default);

    /// <summary>
    /// Remove the entry for (userId, eventSettingsId, dayOffset) from the
    /// row's <c>DayOffs</c> collection. Returns whether an entry was actually
    /// removed (false when no row exists or the offset wasn't present).
    /// </summary>
    Task<bool> RemoveDayOffAsync(
        Guid userId,
        Guid eventSettingsId,
        int dayOffset,
        CancellationToken ct = default);

    /// <summary>
    /// All eligible Build-period signups for the event: rows where
    /// Shift.DayOffset ∈ [BuildStartOffset, 0), the rota's period
    /// is Build or All, and Status ∈ {Confirmed, Pending}.
    /// </summary>
    Task<IReadOnlyList<EligibleBuildSignup>> GetEligibleBuildSignupsAsync(
        Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>
    /// Returns confirmed shift signups whose [StartsAtUtc, EndsAtUtc) overlaps the date range
    /// (in event-local time). When <paramref name="departmentId"/> is non-null, restricts to
    /// shifts whose rota belongs to that team.
    /// </summary>
    Task<IReadOnlyList<ConfirmedShiftRow>> GetConfirmedShiftsInRangeAsync(
        Guid eventSettingsId,
        LocalDate startDate,
        LocalDate endDate,
        Guid? departmentId,
        CancellationToken ct);
}

/// <summary>
/// Projection: just what the gap-detector needs for a single eligible signup.
/// RotaName is the parent rota's display name, used by the heatmap partial
/// to populate cell-click popovers.
/// </summary>
public sealed record EligibleBuildSignup(
    Guid UserId,
    int DayOffset,
    SignupStatus Status,
    string RotaName);

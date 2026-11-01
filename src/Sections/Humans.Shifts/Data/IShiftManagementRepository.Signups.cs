using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Domain;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Settings.Contracts;
using NodaTime;

namespace Humans.Shifts.Data;

/// <summary>
/// <c>ForMutation</c> reads return tracked entities that the same scoped context persists on
/// <c>SaveChangesAsync</c>; every other read is <c>AsNoTracking</c>.
/// </summary>
internal partial interface IShiftManagementRepository
{
    /// <summary>Optionally filtered to one event. Team names come from <c>ITeamService</c>, not here.</summary>
    Task<IReadOnlyList<ShiftSignup>> GetForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        Guid? eventSettingsId = null,
        CancellationToken ct = default);

    Task<ShiftSignup?> GetTeamProbeAsync(
        Guid id, ShiftSignupTeamProbeScope scope, CancellationToken ct = default);

    /// <summary>Includes the shift's sibling signups, for capacity checks.</summary>
    Task<ShiftSignup?> GetByIdForMutationAsync(Guid signupId, CancellationToken ct = default);

    /// <summary>Pending signups in the block, plus Confirmed when <paramref name="scope"/> asks. Includes siblings for capacity checks.</summary>
    Task<List<ShiftSignup>> GetBlockForMutationAsync(
        Guid signupBlockId,
        ShiftSignupBlockMutationScope scope,
        CancellationToken ct = default);

    /// <summary>The subset of <paramref name="shiftIds"/> the user already holds a Pending or Confirmed signup on.</summary>
    Task<HashSet<Guid>> GetActiveShiftIdsForUserAsync(
        Guid userId, IReadOnlyCollection<Guid> shiftIds, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetUserIdsForDayAsync(
        Guid eventSettingsId,
        int dayOffset,
        ShiftDayUserStatusScope statusScope,
        CancellationToken ct = default);

    Task<IReadOnlyList<VolunteerTagPreference>> GetVolunteerTagPreferencesForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>GDPR Art. 17: removes the user's shift-tag preferences.</summary>
    Task<int> DeleteVolunteerTagPreferencesForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17: clears <see cref="ShiftSignup.StatusReason"/>. The row stays as the record of who covered
    /// which shift; the coordinator's free text about the person does not.
    /// </summary>
    Task<int> ClearSignupStatusReasonsForUserAsync(Guid userId, CancellationToken ct = default);

    void AddRange(IEnumerable<ShiftSignup> signups);

    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>Returns each cancelled signup so the caller can audit them one by one.</summary>
    Task<IReadOnlyList<(Guid SignupId, Guid ShiftId)>> CancelActiveSignupsForUserAsync(
        Guid userId, string reason, CancellationToken ct = default);

    Task<int> DeleteAllForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Account-merge fold of volunteer, enrolled-by and reviewed-by references in one save. A source-owned row for a
    /// shift the target already holds is dropped. Returns the touched row count and event ids for audit/cache refresh.
    /// </summary>
    Task<(int Count, IReadOnlyList<Guid> EventIds)> ReassignToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);

    Task<IReadOnlyList<ShiftSignup>> GetAllForOrphanScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Confirmed or Pending signups from <paramref name="buildStartOffset"/> up to day 0 on Build or All rotas.
    /// The offset comes from the caller: a repository must not read app-wide calendar fields itself.
    /// </summary>
    Task<IReadOnlyList<EligibleBuildSignup>> GetEligibleBuildSignupsAsync(
        Guid eventSettingsId, int buildStartOffset, CancellationToken ct = default);

    /// <summary>
    /// Confirmed signups overlapping the range in event-local time. <paramref name="calendar"/> comes from the
    /// caller: a repository must not read app-wide calendar fields itself.
    /// </summary>
    Task<IReadOnlyList<ConfirmedShiftRow>> GetConfirmedShiftsInRangeAsync(
        Guid eventSettingsId,
        IEventSettingsInfo calendar,
        LocalDate startDate,
        LocalDate endDate,
        Guid? departmentId,
        CancellationToken ct);
}

/// <summary><c>RotaName</c> feeds the heatmap's cell-click popovers.</summary>
internal sealed record EligibleBuildSignup(
    Guid UserId,
    int DayOffset,
    SignupStatus Status,
    string RotaName);

internal enum ShiftSignupBlockMutationScope
{
    PendingOnly,
    PendingAndConfirmed
}

internal enum ShiftDayUserStatusScope
{
    ConfirmedOnly,
    PendingOrConfirmed
}

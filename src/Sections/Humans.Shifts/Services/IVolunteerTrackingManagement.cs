using Humans.Shifts.Contracts;

using NodaTime;
using Humans.Base.Interfaces;
using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Services;

internal sealed record GeneralAvailabilitySnapshot(
    Guid UserId,
    Guid EventSettingsId,
    IReadOnlyList<int> AvailableDayOffsets);

internal interface IVolunteerTrackingManagement : IApplicationService, IVolunteerTrackingService
{
    Task<VolunteerTrackingViewModel> GetTrackingDataAsync(CancellationToken ct = default);

    Task<bool> SetAvailabilityAsync(Guid userId, Guid eventSettingsId, IReadOnlyList<int> dayOffsets);

    Task<IReadOnlyList<GeneralAvailabilitySnapshot>> GetAvailableForDayAsync(
        Guid eventSettingsId, int dayOffset, CancellationToken ct = default);

    /// <summary>
    /// Adds or removes one build-day offset. Additions need a known calendar and an offset in its build period;
    /// removals can clear old offsets. Audited against <paramref name="coordinatorUserId"/>.
    /// </summary>
    Task<bool> SetDayAvailabilityAsync(
        Guid userId, Guid eventSettingsId, int dayOffset, bool available,
        Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>
    /// Caller has authorized. Day-offs the new span covers are cleared in the same transaction, each audited, and
    /// returned.
    /// </summary>
    Task<SetCampSetupResult> SetCampSetupAsync(
        Guid targetUserId, LocalDate barrioSetupStartDate,
        string? notes, Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>Caller has authorized. Audited.</summary>
    Task ClearCampSetupAsync(
        Guid targetUserId, Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>
    /// Caller has authorized. Replaces any entry for the day. Camp-setup overlap is not checked server-side: the UI
    /// never renders the action on CampSetup cells.
    /// </summary>
    Task<SetDayOffResult> SetDayOffAsync(
        Guid targetUserId, int dayOffset, string? reason,
        Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>Caller has authorized. Idempotent; audited only when an entry was removed.</summary>
    Task<ClearDayOffResult> ClearDayOffAsync(
        Guid targetUserId, int dayOffset,
        Guid coordinatorUserId, CancellationToken ct = default);
}

internal sealed record SetCampSetupResult(
    bool Ok,
    string? ErrorMessageKey,
    IReadOnlyList<int>? AutoClearedDayOffs);

internal sealed record SetDayOffResult(bool Ok, string? ErrorMessageKey);

internal sealed record ClearDayOffResult(bool Removed);

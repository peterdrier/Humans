using Humans.Application.Architecture;
using Humans.Application.DTOs;
using NodaTime;

namespace Humans.Application.Interfaces.Shifts;

[SurfaceBudget(5)]
public interface IVolunteerTrackingService : IApplicationService
{
    Task<VolunteerTrackingViewModel> GetTrackingDataAsync(CancellationToken ct = default);

    /// <summary>
    /// Coordinator path. Caller has already authorized. When the new
    /// camp-setup span newly covers existing day-off entries, those entries
    /// are silently auto-cleared in the same transaction; the cleared
    /// offsets are returned so the controller can emit one
    /// <see cref="Humans.Domain.Enums.AuditAction.VolunteerDayOffCleared"/>
    /// row per offset alongside the camp-setup audit row.
    /// </summary>
    Task<SetCampSetupResult> SetCampSetupAsync(
        Guid targetUserId, LocalDate barrioSetupStartDate,
        string? notes, Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>Coordinator path. Caller has already authorized.</summary>
    Task ClearCampSetupAsync(
        Guid targetUserId, Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>
    /// Coordinator path. Caller has already authorized. Marks
    /// (targetUserId, dayOffset) as a "day off" with the optional reason.
    /// Replaces any existing entry for that day. Camp-setup overlap is
    /// not validated server-side — the UI prevents it by not rendering
    /// the action on CampSetup cells.
    /// </summary>
    Task<SetDayOffResult> SetDayOffAsync(
        Guid targetUserId, int dayOffset, string? reason,
        Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>
    /// Coordinator path. Caller has already authorized. Idempotent: returns
    /// <c>Removed = false</c> if no entry exists for (userId, dayOffset).
    /// </summary>
    Task<ClearDayOffResult> ClearDayOffAsync(
        Guid targetUserId, int dayOffset,
        Guid coordinatorUserId, CancellationToken ct = default);
}

public sealed record SetCampSetupResult(
    bool Ok,
    string? ErrorMessageKey,
    IReadOnlyList<int>? AutoClearedDayOffs);

public sealed record SetDayOffResult(bool Ok, string? ErrorMessageKey);

public sealed record ClearDayOffResult(bool Removed);

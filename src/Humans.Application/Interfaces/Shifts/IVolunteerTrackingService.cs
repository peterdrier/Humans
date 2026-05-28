using Humans.Application.DTOs;
using NodaTime;

namespace Humans.Application.Interfaces.Shifts;

public record GeneralAvailabilitySnapshot(
    Guid UserId,
    Guid EventSettingsId,
    IReadOnlyList<int> AvailableDayOffsets);

public interface IVolunteerTrackingService : IApplicationService, IVolunteerTrackingServiceRead
{
    Task<VolunteerTrackingViewModel> GetTrackingDataAsync(CancellationToken ct = default);

    Task SetAvailabilityAsync(Guid userId, Guid eventSettingsId, IReadOnlyList<int> dayOffsets);

    Task<IReadOnlyList<GeneralAvailabilitySnapshot>> GetAvailableForDayAsync(
        Guid eventSettingsId, int dayOffset);

    /// <summary>
    /// Adds or removes one build-day offset from the user's declared
    /// availability. Read-modify-write; preserves other offsets; invalidates
    /// the user's shift view cache. Add is a no-op for positive event-day
    /// offsets.
    /// </summary>
    Task<bool> ApplyAvailabilityDayAsync(
        Guid userId, Guid eventSettingsId, int dayOffset, AvailabilityDayAction action,
        CancellationToken ct = default);

    /// <summary>
    /// Coordinator path. Caller has already authorized. Passing null
    /// <paramref name="barrioSetupStartDate"/> clears camp setup. When a new
    /// camp-setup span covers existing day-off entries, those entries are
    /// silently auto-cleared in the same transaction; the cleared offsets are
    /// returned so the controller can emit one
    /// <see cref="Humans.Domain.Enums.AuditAction.VolunteerDayOffCleared"/>
    /// row per offset alongside the camp-setup audit row.
    /// </summary>
    Task<SetCampSetupResult> SetCampSetupAsync(
        Guid targetUserId, LocalDate? barrioSetupStartDate,
        string? notes, Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>
    /// Coordinator path. Caller has already authorized. Applies a set or clear
    /// operation to one build day-off entry. Camp-setup overlap is not
    /// validated server-side; the UI prevents it by not rendering the set
    /// action on CampSetup cells.
    /// </summary>
    Task<DayOffActionResult> ApplyDayOffAsync(
        Guid targetUserId, int dayOffset, VolunteerDayOffAction action, string? reason,
        Guid coordinatorUserId, CancellationToken ct = default);
}

public enum AvailabilityDayAction
{
    Add,
    Remove
}

public enum VolunteerDayOffAction
{
    Set,
    Clear
}

public sealed record SetCampSetupResult(
    bool Ok,
    string? ErrorMessageKey,
    IReadOnlyList<int>? AutoClearedDayOffs);

public sealed record DayOffActionResult(bool Ok, string? ErrorMessageKey, bool Removed);

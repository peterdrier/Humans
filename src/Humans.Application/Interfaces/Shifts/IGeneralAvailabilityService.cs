namespace Humans.Application.Interfaces.Shifts;

public record GeneralAvailabilitySnapshot(
    Guid UserId,
    Guid EventSettingsId,
    IReadOnlyList<int> AvailableDayOffsets);

public interface IGeneralAvailabilityService : IApplicationService
{
    Task SetAvailabilityAsync(Guid userId, Guid eventSettingsId, List<int> dayOffsets);
    Task<GeneralAvailabilitySnapshot?> GetByUserAsync(Guid userId, Guid eventSettingsId);
    Task<IReadOnlyList<GeneralAvailabilitySnapshot>> GetAvailableForDayAsync(Guid eventSettingsId, int dayOffset);
    Task DeleteAsync(Guid userId, Guid eventSettingsId);

    /// <summary>
    /// Add (available=true) or remove (available=false) one build-day offset from
    /// the user's declared availability. Read-modify-write; preserves other
    /// offsets; invalidates the user's shift view cache. No-op for positive
    /// (event-day) offsets — build availability is negative offsets only.
    /// Returns true when the stored state actually changed (so callers can
    /// gate audit emission on real state transitions, matching
    /// <see cref="IVolunteerTrackingService.ClearDayOffAsync"/>'s
    /// <c>Removed</c> bool).
    /// </summary>
    Task<bool> SetDayAvailabilityAsync(
        Guid userId, Guid eventSettingsId, int dayOffset, bool available,
        CancellationToken ct = default);
}

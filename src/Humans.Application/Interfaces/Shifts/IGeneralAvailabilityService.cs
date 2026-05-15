using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Shifts;

public record GeneralAvailabilitySnapshot(
    Guid UserId,
    Guid EventSettingsId,
    IReadOnlyList<int> AvailableDayOffsets);

[SurfaceBudget(4)]
public interface IGeneralAvailabilityService : IApplicationService
{
    Task SetAvailabilityAsync(Guid userId, Guid eventSettingsId, List<int> dayOffsets);
    Task<GeneralAvailabilitySnapshot?> GetByUserAsync(Guid userId, Guid eventSettingsId);
    Task<IReadOnlyList<GeneralAvailabilitySnapshot>> GetAvailableForDayAsync(Guid eventSettingsId, int dayOffset);
    Task DeleteAsync(Guid userId, Guid eventSettingsId);
}

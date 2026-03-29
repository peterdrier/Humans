using Humans.Domain.Entities;

namespace Humans.Application.Interfaces;

public interface IGeneralAvailabilityService
{
    Task SetAvailabilityAsync(Guid userId, Guid eventSettingsId, List<int> dayOffsets);
    Task<GeneralAvailability?> GetByUserAsync(Guid userId, Guid eventSettingsId);
    Task<List<GeneralAvailability>> GetAvailableForDayAsync(Guid eventSettingsId, int dayOffset);
    Task DeleteAsync(Guid userId, Guid eventSettingsId);
}

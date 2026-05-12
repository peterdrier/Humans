using Humans.Application.Architecture;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Shifts;

[SurfaceBudget(4)]
public interface IGeneralAvailabilityService : IApplicationService
{
    Task SetAvailabilityAsync(Guid userId, Guid eventSettingsId, List<int> dayOffsets);
    Task<GeneralAvailability?> GetByUserAsync(Guid userId, Guid eventSettingsId);
    Task<List<GeneralAvailability>> GetAvailableForDayAsync(Guid eventSettingsId, int dayOffset);
    Task DeleteAsync(Guid userId, Guid eventSettingsId);
}

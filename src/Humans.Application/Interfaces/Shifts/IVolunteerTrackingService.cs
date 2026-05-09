using Humans.Application.DTOs;
using NodaTime;

namespace Humans.Application.Interfaces.Shifts;

public interface IVolunteerTrackingService
{
    Task<VolunteerTrackingViewModel> GetTrackingDataAsync(CancellationToken ct = default);

    /// <summary>Coordinator path. Caller has already authorized.</summary>
    Task<SetCampSetupResult> SetCampSetupAsync(
        Guid targetUserId, LocalDate barrioSetupStartDate,
        string? notes, Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>Coordinator path. Caller has already authorized.</summary>
    Task ClearCampSetupAsync(
        Guid targetUserId, Guid coordinatorUserId, CancellationToken ct = default);
}

public sealed record SetCampSetupResult(bool Ok, string? ErrorMessageKey);

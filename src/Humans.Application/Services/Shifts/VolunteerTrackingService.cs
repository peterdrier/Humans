using Humans.Application.DTOs;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Shifts;

public sealed class VolunteerTrackingService : IVolunteerTrackingService
{
    private readonly IVolunteerTrackingRepository _trackingRepo;
    private readonly IShiftManagementRepository _shiftManagement;
    private readonly IGeneralAvailabilityRepository _availability;
    private readonly IUserService _userService;
    private readonly IClock _clock;

    public VolunteerTrackingService(
        IVolunteerTrackingRepository trackingRepo,
        IShiftManagementRepository shiftManagement,
        IGeneralAvailabilityRepository availability,
        IUserService userService,
        IClock clock)
    {
        _trackingRepo = trackingRepo;
        _shiftManagement = shiftManagement;
        _availability = availability;
        _userService = userService;
        _clock = clock;
    }

    public async Task<VolunteerTrackingViewModel> GetTrackingDataAsync(CancellationToken ct = default)
    {
        var es = await _shiftManagement.GetActiveEventSettingsAsync(ct).ConfigureAwait(false);
        if (es is null)
        {
            return new VolunteerTrackingViewModel(
                false,
                0,
                Array.Empty<VolunteerHeatmapRow>(),
                Array.Empty<VolunteerCohortRow>());
        }

        return new VolunteerTrackingViewModel(
            true,
            es.BuildStartOffset,
            Array.Empty<VolunteerHeatmapRow>(),
            Array.Empty<VolunteerCohortRow>());
    }

    public Task<SetCampSetupResult> SetCampSetupAsync(
        Guid targetUserId, LocalDate barrioSetupStartDate, string? notes,
        Guid coordinatorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task ClearCampSetupAsync(
        Guid targetUserId, Guid coordinatorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task<SetBlockResult> SetBlockAsync(
        Guid targetUserId, int dayOffset, bool block,
        Guid coordinatorUserId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task<SaveOwnBlockedDaysResult> SaveOwnBlockedDaysAsync(
        Guid ownerUserId, IReadOnlyList<int> dayOffsets, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");

    public Task<MineBlockedDaysSummary> GetMineBlockedDaysSummaryAsync(
        Guid userId, CancellationToken ct = default)
        => throw new NotSupportedException("Not yet implemented.");
}

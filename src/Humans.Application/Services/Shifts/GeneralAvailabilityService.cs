using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Services.Shifts;

/// <summary>Per-user-per-event availability. No caching decorator (§15 Option A).</summary>
public sealed class GeneralAvailabilityService : IGeneralAvailabilityService, IUserMerge
{
    private readonly IGeneralAvailabilityRepository _repo;
    private readonly IShiftViewInvalidator _viewInvalidator;
    private readonly IClock _clock;

    public GeneralAvailabilityService(
        IGeneralAvailabilityRepository repo,
        IShiftViewInvalidator viewInvalidator,
        IClock clock)
    {
        _repo = repo;
        _viewInvalidator = viewInvalidator;
        _clock = clock;
    }

    public async Task SetAvailabilityAsync(Guid userId, Guid eventSettingsId, List<int> dayOffsets)
    {
        var now = _clock.GetCurrentInstant();
        await _repo.UpsertAsync(userId, eventSettingsId, dayOffsets, now);
        _viewInvalidator.InvalidateUser(userId);
    }

    public async Task<GeneralAvailabilitySnapshot?> GetByUserAsync(Guid userId, Guid eventSettingsId)
    {
        var availability = await _repo.GetByUserAndEventAsync(userId, eventSettingsId);
        return availability is null
            ? null
            : ToSnapshot(availability);
    }

    public async Task<IReadOnlyList<GeneralAvailabilitySnapshot>> GetAvailableForDayAsync(
        Guid eventSettingsId, int dayOffset)
    {
        // EF can't translate List<int>.Contains over jsonb; load all and filter in memory (~500 users).
        var all = await _repo.GetByEventAsync(eventSettingsId);
        return all
            .Where(g => g.AvailableDayOffsets.Contains(dayOffset))
            .Select(ToSnapshot)
            .ToList();
    }

    private static GeneralAvailabilitySnapshot ToSnapshot(GeneralAvailability availability) =>
        new(
            availability.UserId,
            availability.EventSettingsId,
            availability.AvailableDayOffsets);

    public async Task DeleteAsync(Guid userId, Guid eventSettingsId)
    {
        await _repo.DeleteAsync(userId, eventSettingsId);
        _viewInvalidator.InvalidateUser(userId);
    }

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        await _repo.ReassignToUserAsync(sourceUserId, targetUserId, updatedAt, ct);
        _viewInvalidator.InvalidateUser(sourceUserId);
        _viewInvalidator.InvalidateUser(targetUserId);
    }
}

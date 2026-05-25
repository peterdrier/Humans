using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Services.Shifts;

/// <summary>Per-user-per-event availability. No caching decorator (§15 Option A).</summary>
public sealed class GeneralAvailabilityService(
    IGeneralAvailabilityRepository repo,
    IShiftViewInvalidator viewInvalidator,
    IClock clock) : IGeneralAvailabilityService, IUserMerge
{
    public async Task SetAvailabilityAsync(Guid userId, Guid eventSettingsId, List<int> dayOffsets)
    {
        var now = clock.GetCurrentInstant();
        await repo.UpsertAsync(userId, eventSettingsId, dayOffsets, now);
        viewInvalidator.InvalidateUser(userId);
    }

    public async Task<GeneralAvailabilitySnapshot?> GetByUserAsync(Guid userId, Guid eventSettingsId)
    {
        var availability = await repo.GetByUserAndEventAsync(userId, eventSettingsId);
        return availability is null
            ? null
            : ToSnapshot(availability);
    }

    public async Task<IReadOnlyList<GeneralAvailabilitySnapshot>> GetAvailableForDayAsync(
        Guid eventSettingsId, int dayOffset)
    {
        // EF can't translate List<int>.Contains over jsonb; load all and filter in memory (~500 users).
        var all = await repo.GetByEventAsync(eventSettingsId);
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
        await repo.DeleteAsync(userId, eventSettingsId);
        viewInvalidator.InvalidateUser(userId);
    }

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        await repo.ReassignToUserAsync(sourceUserId, targetUserId, updatedAt, ct);
        viewInvalidator.InvalidateUser(sourceUserId);
        viewInvalidator.InvalidateUser(targetUserId);
    }
}

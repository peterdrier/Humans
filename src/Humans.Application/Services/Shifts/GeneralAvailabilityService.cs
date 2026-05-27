using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Services.Shifts;

/// <summary>Per-user-per-event availability. No caching decorator (§15 Option A).</summary>
public sealed class GeneralAvailabilityService(
    IVolunteerTrackingRepository repo,
    IShiftViewInvalidator viewInvalidator,
    IClock clock) : IGeneralAvailabilityService, IUserMerge
{
    public async Task SetAvailabilityAsync(Guid userId, Guid eventSettingsId, List<int> dayOffsets)
    {
        var now = clock.GetCurrentInstant();
        await repo.UpsertAvailabilityAsync(userId, eventSettingsId, dayOffsets, now);
        viewInvalidator.InvalidateUser(userId);
    }

    public async Task<GeneralAvailabilitySnapshot?> GetByUserAsync(Guid userId, Guid eventSettingsId)
    {
        var availability = await repo.GetAvailabilityByUserAndEventAsync(userId, eventSettingsId);
        return availability is null
            ? null
            : ToSnapshot(availability);
    }

    public async Task<IReadOnlyList<GeneralAvailabilitySnapshot>> GetAvailableForDayAsync(
        Guid eventSettingsId, int dayOffset)
    {
        // EF can't translate List<int>.Contains over jsonb; load all and filter in memory (~500 users).
        var all = await repo.GetAvailabilityByEventAsync(eventSettingsId);
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
        await repo.DeleteAvailabilityAsync(userId, eventSettingsId);
        viewInvalidator.InvalidateUser(userId);
    }

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        await repo.ReassignAvailabilityToUserAsync(sourceUserId, targetUserId, updatedAt, ct);
        viewInvalidator.InvalidateUser(sourceUserId);
        viewInvalidator.InvalidateUser(targetUserId);
    }
}

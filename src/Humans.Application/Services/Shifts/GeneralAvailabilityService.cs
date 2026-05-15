using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Services.Shifts;

/// <summary>
/// Application-layer implementation of <see cref="IGeneralAvailabilityService"/>.
/// Goes through <see cref="IGeneralAvailabilityRepository"/> for all data
/// access — this type never imports <c>Microsoft.EntityFrameworkCore</c>,
/// enforced by <c>Humans.Application.csproj</c>'s reference graph.
/// </summary>
/// <remarks>
/// <para>
/// No caching decorator (§15 Option A): canonical availability data is a
/// small, per-user-per-event store that is read and written on the same
/// request flows (coordinator search, volunteer self-service). A decorator
/// is not warranted — same rationale used by Users (#243), Governance (#242),
/// Budget (#544), City Planning (#543), and Audit Log (#552).
/// </para>
/// <para>
/// Scope note: only the <c>GeneralAvailabilityService</c> migration lands
/// here. <c>ShiftManagementService</c> and <c>ShiftSignupService</c> remain
/// in <c>Humans.Infrastructure/Services</c> and continue to access
/// <c>DbContext</c> directly until the follow-up sub-tasks (#541a, #541b).
/// </para>
/// </remarks>
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
        // EF Core can't translate List<int>.Contains inside a LINQ query for
        // jsonb, so we load all records for the event and filter in memory.
        // At ~500 users max this is fine.
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

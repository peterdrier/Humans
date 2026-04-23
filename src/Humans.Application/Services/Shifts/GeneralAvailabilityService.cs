using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
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
public sealed class GeneralAvailabilityService : IGeneralAvailabilityService
{
    private readonly IGeneralAvailabilityRepository _repo;
    private readonly IClock _clock;

    public GeneralAvailabilityService(
        IGeneralAvailabilityRepository repo,
        IClock clock)
    {
        _repo = repo;
        _clock = clock;
    }

    public Task SetAvailabilityAsync(Guid userId, Guid eventSettingsId, List<int> dayOffsets)
    {
        var now = _clock.GetCurrentInstant();
        return _repo.UpsertAsync(userId, eventSettingsId, dayOffsets, now);
    }

    public Task<GeneralAvailability?> GetByUserAsync(Guid userId, Guid eventSettingsId) =>
        _repo.GetByUserAndEventAsync(userId, eventSettingsId);

    public async Task<List<GeneralAvailability>> GetAvailableForDayAsync(
        Guid eventSettingsId, int dayOffset)
    {
        // EF Core can't translate List<int>.Contains inside a LINQ query for
        // jsonb, so we load all records for the event and filter in memory.
        // At ~500 users max this is fine.
        var all = await _repo.GetByEventAsync(eventSettingsId);
        return all.Where(g => g.AvailableDayOffsets.Contains(dayOffset)).ToList();
    }

    public Task DeleteAsync(Guid userId, Guid eventSettingsId) =>
        _repo.DeleteAsync(userId, eventSettingsId);
}

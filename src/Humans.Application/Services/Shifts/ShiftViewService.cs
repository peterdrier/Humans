using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;

namespace Humans.Application.Services.Shifts;

/// <summary>Inner <see cref="IShiftView"/> — direct repo reads, no caching. CachingShiftViewService wraps it (#720).</summary>
public sealed class ShiftViewService : IShiftView
{
    private readonly IShiftManagementRepository _management;
    private readonly IShiftSignupRepository _signups;
    private readonly IVolunteerTrackingRepository _tracking;

    public ShiftViewService(
        IShiftManagementRepository management,
        IShiftSignupRepository signups,
        IVolunteerTrackingRepository tracking)
    {
        _management = management;
        _signups = signups;
        _tracking = tracking;
    }

    public async ValueTask<ShiftUserView> GetUserAsync(Guid userId, CancellationToken ct = default)
    {
        var activeEvent = await _management.GetActiveEventSettingsAsync(ct).ConfigureAwait(false);

        var profile = await _management.GetVolunteerEventProfileAsync(userId, ct).ConfigureAwait(false);
        var tagPrefs = await _signups.GetVolunteerTagPreferencesForUserAsync(userId, ct).ConfigureAwait(false);

        GeneralAvailability? availability = null;
        VolunteerBuildStatus? buildStatus = null;
        IReadOnlyList<ShiftSignup> signups = [];
        if (activeEvent is not null)
        {
            availability = await _tracking
                .GetAvailabilityByUserAndEventAsync(userId, activeEvent.Id, ct).ConfigureAwait(false);
            buildStatus = await _tracking
                .GetAsync(userId, activeEvent.Id, ct).ConfigureAwait(false);
            signups = await _signups
                .GetByUserAsync(userId, activeEvent.Id, ct).ConfigureAwait(false);
        }

        return new ShiftUserView(
            userId,
            profile,
            availability,
            buildStatus,
            tagPrefs,
            signups);
    }

    /// <summary>
    /// True bulk: one query per contributing table filtered by the supplied
    /// user ids (plus active event scope where relevant). Materializes a
    /// <see cref="ShiftUserView"/> for every requested id — users with no
    /// shift-section rows get a view whose fields are null/empty rather than
    /// being absent from the result. Collapses the per-user 6× fan-out that
    /// dominated /Admin first-hit before issue #720.
    /// </summary>
    public async ValueTask<IReadOnlyDictionary<Guid, ShiftUserView>> GetUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = (userIds as IReadOnlyCollection<Guid>) ?? userIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, ShiftUserView>();

        var activeEvent = await _management.GetActiveEventSettingsAsync(ct).ConfigureAwait(false);

        var profiles = await _management.GetVolunteerEventProfilesByUserIdsAsync(ids, ct).ConfigureAwait(false);
        var profileByUser = profiles.ToDictionary(p => p.UserId);

        var tagPrefs = await _signups.GetVolunteerTagPreferencesByUserIdsAsync(ids, ct).ConfigureAwait(false);
        var tagPrefsByUser = tagPrefs
            .GroupBy(t => t.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<VolunteerTagPreference>)g.ToList());

        Dictionary<Guid, GeneralAvailability> availabilityByUser = [];
        Dictionary<Guid, VolunteerBuildStatus> buildStatusByUser = [];
        Dictionary<Guid, IReadOnlyList<ShiftSignup>> signupsByUser = [];

        if (activeEvent is not null)
        {
            var avail = await _tracking
                .GetAvailabilityByUsersAndEventAsync(ids, activeEvent.Id, ct).ConfigureAwait(false);
            availabilityByUser = avail.ToDictionary(a => a.UserId);

            var builds = await _tracking
                .GetByUsersAndEventAsync(ids, activeEvent.Id, ct).ConfigureAwait(false);
            buildStatusByUser = builds.ToDictionary(b => b.UserId);

            var batchSignups = await _signups
                .GetByUsersAndEventAsync(ids, activeEvent.Id, ct).ConfigureAwait(false);
            signupsByUser = batchSignups
                .GroupBy(s => s.UserId)
                // Match GetByUserAsync's per-user ordering for shape parity across the
                // single-user and batch paths. Sort in-memory after the bulk read so the
                // repo stays display-sort-free (memory/architecture/display-sort-in-controllers.md).
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<ShiftSignup>)g
                        .OrderBy(s => s.Shift.DayOffset)
                        .ThenBy(s => s.Shift.StartTime)
                        .ToList());
        }

        var result = new Dictionary<Guid, ShiftUserView>(ids.Count);
        foreach (var id in ids)
        {
            if (result.ContainsKey(id)) continue;
            result[id] = new ShiftUserView(
                id,
                Profile: profileByUser.GetValueOrDefault(id),
                Availability: availabilityByUser.GetValueOrDefault(id),
                BuildStatus: buildStatusByUser.GetValueOrDefault(id),
                TagPreferences: tagPrefsByUser.GetValueOrDefault(id) ?? [],
                Signups: signupsByUser.GetValueOrDefault(id) ?? []);
        }
        return result;
    }

    public async ValueTask<ShiftRotaView> GetRotaAsync(Guid rotaId, CancellationToken ct = default)
    {
        var rota = await _management.GetRotaForViewAsync(rotaId, ct).ConfigureAwait(false);
        if (rota is null)
            return ShiftRotaView.Empty(rotaId);

        var shifts = rota.Shifts.ToList();
        var tags = rota.Tags.ToList();
        var signups = shifts.SelectMany(s => s.ShiftSignups).ToList();

        return new ShiftRotaView(rotaId, rota, shifts, tags, signups);
    }

    public async ValueTask<IReadOnlyDictionary<Guid, ShiftRotaView>> GetRotasAsync(
        IEnumerable<Guid> rotaIds, CancellationToken ct = default)
    {
        var ids = rotaIds as IList<Guid> ?? rotaIds.Distinct().ToList();
        var result = new Dictionary<Guid, ShiftRotaView>(ids.Count);
        foreach (var id in ids)
        {
            if (!result.ContainsKey(id))
                result[id] = await GetRotaAsync(id, ct).ConfigureAwait(false);
        }
        return result;
    }
}

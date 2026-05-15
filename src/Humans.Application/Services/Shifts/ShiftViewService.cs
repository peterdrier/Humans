using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;

namespace Humans.Application.Services.Shifts;

/// <summary>
/// Undecorated (inner) <see cref="IShiftView"/> implementation. Builds the
/// view records directly from repositories on every call — no caching layer.
/// Resolved by the Singleton <c>CachingShiftViewService</c> on cache miss /
/// refresh via the keyed registration (<c>CachingShiftViewService.InnerServiceKey</c>).
/// Issue #720.
/// </summary>
public sealed class ShiftViewService : IShiftView
{
    private readonly IShiftManagementRepository _management;
    private readonly IShiftSignupRepository _signups;
    private readonly IGeneralAvailabilityRepository _availability;
    private readonly IVolunteerTrackingRepository _tracking;

    public ShiftViewService(
        IShiftManagementRepository management,
        IShiftSignupRepository signups,
        IGeneralAvailabilityRepository availability,
        IVolunteerTrackingRepository tracking)
    {
        _management = management;
        _signups = signups;
        _availability = availability;
        _tracking = tracking;
    }

    public async ValueTask<ShiftUserView> GetUserAsync(Guid userId, CancellationToken ct = default)
    {
        var activeEvent = await _management.GetActiveEventSettingsAsync(ct).ConfigureAwait(false);

        var profile = await _management.GetVolunteerEventProfileAsync(userId, ct).ConfigureAwait(false);
        var tagPrefs = await _signups.GetVolunteerTagPreferencesForUserAsync(userId, ct).ConfigureAwait(false);

        GeneralAvailability? availability = null;
        VolunteerBuildStatus? buildStatus = null;
        IReadOnlyList<ShiftSignup> signups = Array.Empty<ShiftSignup>();
        if (activeEvent is not null)
        {
            availability = await _availability
                .GetByUserAndEventAsync(userId, activeEvent.Id, ct).ConfigureAwait(false);
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

    public async ValueTask<IReadOnlyDictionary<Guid, ShiftUserView>> GetUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds as IList<Guid> ?? userIds.Distinct().ToList();
        var result = new Dictionary<Guid, ShiftUserView>(ids.Count);
        foreach (var id in ids)
        {
            if (!result.ContainsKey(id))
                result[id] = await GetUserAsync(id, ct).ConfigureAwait(false);
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

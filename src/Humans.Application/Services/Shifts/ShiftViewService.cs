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
/// <remarks>
/// <see cref="IShiftView"/> is intentionally synchronous (see the interface
/// remarks: "consumers compute what they need from the raw rows", no <c>await</c>
/// ceremony at call sites). Repository calls underneath are async, so each
/// public method awaits internally via <c>.GetAwaiter().GetResult()</c>. This
/// is safe at ~500-user single-server scale (ASP.NET Core has no captured
/// <see cref="System.Threading.SynchronizationContext"/>) and only runs on
/// the rare cache-miss path — cache hits short-circuit in the Singleton
/// decorator without entering this service at all. The VSTHRD002 suppressions
/// are scoped to that one pattern.
/// </remarks>
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

    public ShiftUserView GetUser(Guid userId) =>
#pragma warning disable VSTHRD002 // sync wrapper around async repo calls — see class remarks
        BuildUserAsync(userId).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

    public IReadOnlyDictionary<Guid, ShiftUserView> GetUsers(IEnumerable<Guid> userIds)
    {
        var ids = userIds as IList<Guid> ?? userIds.Distinct().ToList();
        var result = new Dictionary<Guid, ShiftUserView>(ids.Count);
        foreach (var id in ids)
        {
            if (!result.ContainsKey(id))
                result[id] = GetUser(id);
        }
        return result;
    }

    public ShiftRotaView GetRota(Guid rotaId) =>
#pragma warning disable VSTHRD002 // sync wrapper around async repo calls — see class remarks
        BuildRotaAsync(rotaId).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

    public IReadOnlyDictionary<Guid, ShiftRotaView> GetRotas(IEnumerable<Guid> rotaIds)
    {
        var ids = rotaIds as IList<Guid> ?? rotaIds.Distinct().ToList();
        var result = new Dictionary<Guid, ShiftRotaView>(ids.Count);
        foreach (var id in ids)
        {
            if (!result.ContainsKey(id))
                result[id] = GetRota(id);
        }
        return result;
    }

    private async Task<ShiftUserView> BuildUserAsync(Guid userId)
    {
        var activeEvent = await _management.GetActiveEventSettingsAsync().ConfigureAwait(false);

        var profile = await _management.GetVolunteerEventProfileAsync(userId).ConfigureAwait(false);
        var tagPrefs = await _signups.GetVolunteerTagPreferencesForUserAsync(userId).ConfigureAwait(false);
        var allSignups = await _signups.GetByUserAsync(userId).ConfigureAwait(false);

        GeneralAvailability? availability = null;
        VolunteerBuildStatus? buildStatus = null;
        if (activeEvent is not null)
        {
            availability = await _availability
                .GetByUserAndEventAsync(userId, activeEvent.Id).ConfigureAwait(false);
            buildStatus = await _tracking
                .GetAsync(userId, activeEvent.Id).ConfigureAwait(false);
        }

        return new ShiftUserView(
            userId,
            profile,
            availability,
            buildStatus,
            tagPrefs,
            allSignups);
    }

    private async Task<ShiftRotaView> BuildRotaAsync(Guid rotaId)
    {
        var rota = await _management.GetRotaForViewAsync(rotaId).ConfigureAwait(false);
        if (rota is null)
            return ShiftRotaView.Empty(rotaId);

        var shifts = rota.Shifts.ToList();
        var tags = rota.Tags.ToList();
        var signups = shifts.SelectMany(s => s.ShiftSignups).ToList();

        return new ShiftRotaView(rotaId, rota, shifts, tags, signups);
    }
}

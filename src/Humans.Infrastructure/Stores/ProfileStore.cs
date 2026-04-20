using System.Collections.Concurrent;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Stores;

namespace Humans.Infrastructure.Stores;

/// <summary>
/// Dictionary-backed canonical store for <see cref="CachedProfile"/>.
/// Registered as a DI singleton; warmed on startup via
/// <c>ProfileStoreWarmupHostedService</c>.
/// </summary>
public sealed class ProfileStore : IProfileStore
{
    private readonly ConcurrentDictionary<Guid, CachedProfile> _byUserId = new();
    private readonly ConcurrentDictionary<Guid, Guid> _userIdByProfileId = new();

    public CachedProfile? GetByUserId(Guid userId) =>
        _byUserId.TryGetValue(userId, out var profile) ? profile : null;

    public IReadOnlyList<CachedProfile> GetAll() => _byUserId.Values.ToList();

    public void Upsert(Guid userId, CachedProfile profile)
    {
        // Remove stale reverse index entry if this userId previously had a different profileId
        if (_byUserId.TryGetValue(userId, out var existing))
            _userIdByProfileId.TryRemove(existing.ProfileId, out _);

        _byUserId[userId] = profile;
        _userIdByProfileId[profile.ProfileId] = userId;
    }

    public void Remove(Guid userId)
    {
        if (_byUserId.TryRemove(userId, out var profile))
            _userIdByProfileId.TryRemove(profile.ProfileId, out _);
    }

    /// <inheritdoc/>
    public Guid? GetUserIdByProfileId(Guid profileId) =>
        _userIdByProfileId.TryGetValue(profileId, out var userId) ? userId : null;

    /// <summary>
    /// Replaces the entire store with <paramref name="profiles"/>. There is
    /// a brief window between <c>Clear()</c> and the final insert during
    /// which concurrent readers see an empty store — this method is therefore
    /// <b>startup-only</b> and must never be called after the warmup hosted
    /// service has completed and the host has begun serving requests.
    /// </summary>
    public void LoadAll(IReadOnlyDictionary<Guid, CachedProfile> profiles)
    {
        _byUserId.Clear();
        _userIdByProfileId.Clear();
        foreach (var (userId, profile) in profiles)
        {
            _byUserId[userId] = profile;
            _userIdByProfileId[profile.ProfileId] = userId;
        }
    }
}

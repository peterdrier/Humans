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

    public CachedProfile? GetByUserId(Guid userId) =>
        _byUserId.TryGetValue(userId, out var profile) ? profile : null;

    public IReadOnlyList<CachedProfile> GetAll() => _byUserId.Values.ToList();

    public void Upsert(Guid userId, CachedProfile profile) =>
        _byUserId[userId] = profile;

    public void Remove(Guid userId) => _byUserId.TryRemove(userId, out _);

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
        foreach (var (userId, profile) in profiles)
        {
            _byUserId[userId] = profile;
        }
    }
}

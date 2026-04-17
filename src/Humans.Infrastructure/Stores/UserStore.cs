using System.Collections.Concurrent;
using Humans.Application.Interfaces.Stores;

namespace Humans.Infrastructure.Stores;

/// <summary>
/// Dictionary-backed canonical store for <see cref="CachedUser"/>.
/// Registered as a DI singleton; warmed on startup via
/// <c>UserStoreWarmupHostedService</c>.
/// </summary>
/// <remarks>
/// Maintains two indexes:
/// <list type="bullet">
///   <item><c>_byId</c> — primary, keyed by <see cref="CachedUser.Id"/>.</item>
///   <item><c>_byEmailNormalized</c> — secondary, keyed by lowercase-invariant
///   email. Entries with null/empty email are not indexed here.</item>
/// </list>
/// Per the single-writer invariant in <see cref="IUserStore"/>, only
/// <c>UserService</c> mutates this store, so the two indexes stay
/// consistent without external locking beyond the <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// primitives.
/// </remarks>
public sealed class UserStore : IUserStore
{
    private readonly ConcurrentDictionary<Guid, CachedUser> _byId = new();
    private readonly ConcurrentDictionary<string, Guid> _byEmailNormalized =
        new(StringComparer.Ordinal);

    public CachedUser? GetById(Guid userId) =>
        _byId.TryGetValue(userId, out var user) ? user : null;

    public CachedUser? GetByEmail(string email)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null) return null;
        return _byEmailNormalized.TryGetValue(normalized, out var id) && _byId.TryGetValue(id, out var user)
            ? user
            : null;
    }

    public IReadOnlyList<CachedUser> GetAll() => _byId.Values.ToList();

    public void Upsert(CachedUser user)
    {
        // If an existing entry has a different email, evict its old email
        // index entry first so stale secondary lookups cannot hit.
        if (_byId.TryGetValue(user.Id, out var existing))
        {
            var oldNormalized = NormalizeEmail(existing.Email);
            var newNormalized = NormalizeEmail(user.Email);
            if (oldNormalized is not null && !string.Equals(oldNormalized, newNormalized, StringComparison.Ordinal))
            {
                // Only remove if it still points at this user — another
                // user may have already claimed that email key.
                _byEmailNormalized.TryRemove(
                    new KeyValuePair<string, Guid>(oldNormalized, user.Id));
            }
        }

        _byId[user.Id] = user;

        var normalized = NormalizeEmail(user.Email);
        if (normalized is not null)
        {
            _byEmailNormalized[normalized] = user.Id;
        }
    }

    public void Remove(Guid userId)
    {
        if (_byId.TryRemove(userId, out var existing))
        {
            var normalized = NormalizeEmail(existing.Email);
            if (normalized is not null)
            {
                _byEmailNormalized.TryRemove(
                    new KeyValuePair<string, Guid>(normalized, userId));
            }
        }
    }

    /// <summary>
    /// Replaces the entire store with <paramref name="users"/>. There is
    /// a brief window between <c>Clear()</c> and the final insert during
    /// which concurrent readers see an empty store — this method is therefore
    /// <b>startup-only</b> and must never be called after the warmup hosted
    /// service has completed and the host has begun serving requests.
    /// </summary>
    public void LoadAll(IReadOnlyCollection<CachedUser> users)
    {
        _byId.Clear();
        _byEmailNormalized.Clear();
        foreach (var user in users)
        {
            _byId[user.Id] = user;
            var normalized = NormalizeEmail(user.Email);
            if (normalized is not null)
            {
                _byEmailNormalized[normalized] = user.Id;
            }
        }
    }

    private static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
}

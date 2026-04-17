using Humans.Application;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;

namespace Humans.Infrastructure.Services.Users;

/// <summary>
/// Caching decorator for <see cref="IUserService"/>. Caches per-user
/// <see cref="User"/> reads (<see cref="GetByIdAsync"/> and
/// <see cref="GetByIdsAsync"/>) using an <see cref="IMemoryCache"/>-backed
/// 2-minute TTL per-user entry, mirroring the pattern used by
/// <c>CachingProfileService</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Staleness note:</b> <see cref="User"/> inherits from
/// <c>IdentityUser&lt;Guid&gt;</c> and carries security-sensitive fields
/// (SecurityStamp, ConcurrencyStamp). Caching is safe for the service's
/// existing consumers because they use the cached entity only for display
/// or notification data (DisplayName, Email, GoogleEmail, PreferredLanguage).
/// Security-sensitive flows (sign-in, password reset) use
/// <c>UserManager.FindByIdAsync</c>, which does its own load and does not
/// go through this decorator. Same risk profile as
/// <c>CachingProfileService</c>.
/// </para>
/// <para>
/// Low-volume / admin-only / batch-shaped reads (<see cref="GetAllUsersAsync"/>,
/// <see cref="GetByEmailOrAlternateAsync"/>, <see cref="GetContactUsersAsync"/>,
/// and the <c>EventParticipation</c> queries) pass through. Per-user write
/// methods remove the cached entry so the next read repopulates from the
/// inner service.
/// </para>
/// </remarks>
public sealed class CachingUserService : IUserService
{
    private readonly IUserService _inner;
    private readonly IMemoryCache _cache;

    private static readonly TimeSpan UserCacheTtl = TimeSpan.FromMinutes(2);

    public CachingUserService(IUserService inner, IMemoryCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Reads — per-user cache + pass-through
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default)
    {
        var cacheKey = CacheKeys.User(userId);
        if (_cache.TryGetExistingValue<User>(cacheKey, out var cached))
            return cached;

        var user = await _inner.GetByIdAsync(userId, ct);
        if (user is not null)
            _cache.Set(cacheKey, user, UserCacheTtl);

        return user;
    }

    public async Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, User>();

        var result = new Dictionary<Guid, User>(userIds.Count);
        var misses = new List<Guid>();

        foreach (var id in userIds)
        {
            if (_cache.TryGetExistingValue<User>(CacheKeys.User(id), out var cached))
                result[id] = cached;
            else
                misses.Add(id);
        }

        if (misses.Count == 0)
            return result;

        var fetched = await _inner.GetByIdsAsync(misses, ct);
        foreach (var (id, user) in fetched)
        {
            _cache.Set(CacheKeys.User(id), user, UserCacheTtl);
            result[id] = user;
        }

        return result;
    }

    // Low-volume / admin-only / batch-shaped reads — pass through
    public Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken ct = default) =>
        _inner.GetAllUsersAsync(ct);

    public Task<User?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default) =>
        _inner.GetByEmailOrAlternateAsync(email, ct);

    public Task<IReadOnlyList<User>> GetContactUsersAsync(string? search, CancellationToken ct = default) =>
        _inner.GetContactUsersAsync(search, ct);

    public Task<EventParticipation?> GetParticipationAsync(Guid userId, int year, CancellationToken ct = default) =>
        _inner.GetParticipationAsync(userId, year, ct);

    public Task<List<EventParticipation>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default) =>
        _inner.GetAllParticipationsForYearAsync(year, ct);

    // ──────────────────────────────────────────────────────────────────────────
    // Writes — delegate then invalidate the per-user cache entry
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default)
    {
        var result = await _inner.TrySetGoogleEmailAsync(userId, email, ct);
        if (result)
            _cache.Remove(CacheKeys.User(userId));
        return result;
    }

    public async Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default)
    {
        await _inner.UpdateDisplayNameAsync(userId, displayName, ct);
        _cache.Remove(CacheKeys.User(userId));
    }

    public async Task<bool> SetDeletionPendingAsync(Guid userId, Instant requestedAt, Instant scheduledFor, CancellationToken ct = default)
    {
        var result = await _inner.SetDeletionPendingAsync(userId, requestedAt, scheduledFor, ct);
        if (result)
            _cache.Remove(CacheKeys.User(userId));
        return result;
    }

    public async Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var result = await _inner.ClearDeletionAsync(userId, ct);
        if (result)
            _cache.Remove(CacheKeys.User(userId));
        return result;
    }

    // EventParticipation writes do not affect cached User entities, but keep
    // invalidation symmetrical for anything that later includes participation
    // data in the User projection.
    public async Task<EventParticipation> DeclareNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var result = await _inner.DeclareNotAttendingAsync(userId, year, ct);
        _cache.Remove(CacheKeys.User(userId));
        return result;
    }

    public async Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default)
    {
        var result = await _inner.UndoNotAttendingAsync(userId, year, ct);
        if (result)
            _cache.Remove(CacheKeys.User(userId));
        return result;
    }

    public async Task SetParticipationFromTicketSyncAsync(Guid userId, int year, ParticipationStatus status, CancellationToken ct = default)
    {
        await _inner.SetParticipationFromTicketSyncAsync(userId, year, status, ct);
        _cache.Remove(CacheKeys.User(userId));
    }

    public async Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default)
    {
        await _inner.RemoveTicketSyncParticipationAsync(userId, year, ct);
        _cache.Remove(CacheKeys.User(userId));
    }

    // Batch backfill affects EventParticipations, not User rows directly. User
    // cache entries do not contain participation data, so no bulk invalidation
    // is needed here — the existing 2-min TTL keeps any derived views fresh.
    public Task<int> BackfillParticipationsAsync(int year, List<(Guid UserId, ParticipationStatus Status)> entries, CancellationToken ct = default) =>
        _inner.BackfillParticipationsAsync(year, entries, ct);
}

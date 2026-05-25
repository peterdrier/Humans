using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.EarlyEntry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.EarlyEntry;

/// <summary>
/// Singleton caching decorator for <see cref="IEarlyEntryService"/>. Caches the
/// per-user stub read (<see cref="GetForUserAsync"/>), including the negative
/// (no-EE) result so the no-EE majority does not re-fan-out on every render.
/// <see cref="GetRosterAsync"/> always delegates to the inner service (the admin
/// roster must see live data). No startup warmup — cold-loaded on first read.
/// EE is derived, so external write paths evict via <see cref="IEarlyEntryInvalidator"/>.
/// </summary>
public sealed class CachingEarlyEntryService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingEarlyEntryService> logger)
    : TrackedCache<Guid, UserEarlyEntry?>("EarlyEntry.UserEarlyEntry", warmOnStartup: false, logger),
        IEarlyEntryService, IEarlyEntryInvalidator
{
    /// <summary>
    /// DI service key under which the undecorated (inner) <see cref="IEarlyEntryService"/>
    /// is registered. Used by the Singleton decorator to resolve the Scoped inner
    /// service per-call without triggering self-resolution on the unkeyed
    /// <see cref="IEarlyEntryService"/> registration.
    /// </summary>
    public const string InnerServiceKey = "early-entry-inner";

    public async Task<UserEarlyEntry?> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        if (TryGet(userId, out var cached)) return cached; // cached may be null (negative)

        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IEarlyEntryService>(InnerServiceKey);
        var result = await inner.GetForUserAsync(userId, ct);
        Set(userId, result);
        return result;
    }

    public async Task<IReadOnlyList<EarlyEntryRosterRow>> GetRosterAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IEarlyEntryService>(InnerServiceKey);
        return await inner.GetRosterAsync(ct);
    }

    public void InvalidateUser(Guid userId) => Invalidate(userId);

    public void InvalidateAll() => Clear();
}

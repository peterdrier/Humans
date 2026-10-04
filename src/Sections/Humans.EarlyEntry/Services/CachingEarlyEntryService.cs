using Humans.Base.Caching;
using Humans.EarlyEntry.Contracts;
using Humans.Settings.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.EarlyEntry.Services;

/// <summary>
/// Caches <see cref="GetForUserAsync"/> per person, negative results included — "no early
/// entry" is the common answer and must be remembered. <see cref="GetRosterAsync"/> is
/// always live.
/// </summary>
internal sealed class CachingEarlyEntryService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingEarlyEntryService> logger)
    : TrackedCache<Guid, UserEarlyEntry?>("EarlyEntry.UserEarlyEntry", warmOnStartup: false, logger),
        IEarlyEntryService, IEarlyEntryInvalidator, IEventSettingsChangeListener
{
    private readonly Lock _cacheGate = new();
    private long _cacheGeneration;

    /// <summary>Key for the undecorated inner service. Unkeyed, this Singleton would resolve itself.</summary>
    public const string InnerServiceKey = "early-entry-inner";

    public async Task<UserEarlyEntry?> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        long generation;
        lock (_cacheGate)
        {
            if (TryGet(userId, out var cached)) return cached; // cached may be null (negative)
            generation = _cacheGeneration;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IEarlyEntryService>(InnerServiceKey);
        var result = await inner.GetForUserAsync(userId, ct);
        lock (_cacheGate)
        {
            // An eviction may have run while the provider fan-out was loading.
            if (generation == _cacheGeneration)
                Set(userId, result);
        }
        return result;
    }

    public async Task<IReadOnlyList<EarlyEntryRosterRow>> GetRosterAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IEarlyEntryService>(InnerServiceKey);
        return await inner.GetRosterAsync(ct);
    }

    public void InvalidateUser(Guid userId)
    {
        lock (_cacheGate)
        {
            _cacheGeneration++;
            Invalidate(userId);
        }
    }

    public void InvalidateAll()
    {
        lock (_cacheGate)
        {
            _cacheGeneration++;
            Clear();
        }
    }

    /// <summary>
    /// The gate date and <c>EarlyEntryStartOffset</c> move every holder's entry date at
    /// once, so an event-settings save drops the whole cache. Nothing here is keyed by
    /// event, so the id is not needed.
    /// </summary>
    public void EventSettingsChanged(Guid eventSettingsId) => InvalidateAll();
}

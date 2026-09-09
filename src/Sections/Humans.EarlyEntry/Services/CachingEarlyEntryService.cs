using Humans.Base.Caching;
using Humans.EarlyEntry.Contracts;
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
        IEarlyEntryService, IEarlyEntryInvalidator
{
    /// <summary>Key for the undecorated inner service. Unkeyed, this Singleton would resolve itself.</summary>
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

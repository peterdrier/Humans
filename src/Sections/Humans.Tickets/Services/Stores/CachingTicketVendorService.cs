using Humans.Base.Caching;
using Humans.Base.Interfaces.Caching;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Tickets.Services.Stores;

/// <summary>
/// Singleton caching decorator over <see cref="ITicketVendorService"/>. Caches only
/// <see cref="GetEventSummaryAsync"/> — the one high-read, low-churn vendor call; every
/// other member forwards straight through to the keyed inner, uncached.
/// </summary>
internal sealed class CachingTicketVendorService : ITicketVendorService, ITicketVendorCacheInvalidator
{
    private static readonly Duration EventSummaryCacheTtl = Duration.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EventSummaryCache _eventSummaries;

    public CachingTicketVendorService(
        IServiceScopeFactory scopeFactory,
        IClock clock,
        ILogger<CachingTicketVendorService> logger)
    {
        _scopeFactory = scopeFactory;
        _eventSummaries = new EventSummaryCache(scopeFactory, clock, EventSummaryCacheTtl, logger);
    }

    public ICacheStats EventSummaryCacheStats => _eventSummaries;

    public async Task<VendorEventSummaryDto> GetEventSummaryAsync(
        string eventId, CancellationToken ct = default)
    {
        var summary = await _eventSummaries.GetSummaryAsync(eventId, ct);
        return summary ?? throw new InvalidOperationException(
            $"Vendor event summary load for '{eventId}' returned no result.");
    }

    public Task<IReadOnlyList<VendorOrderDto>> GetOrdersAsync(
        Instant? since, string eventId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetOrdersAsync(since, eventId, ct));

    public Task<IReadOnlyList<VendorTicketDto>> GetIssuedTicketsAsync(
        Instant? since, string eventId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetIssuedTicketsAsync(since, eventId, ct));

    public Task<IReadOnlyList<VendorCheckInDto>> GetCheckInsAsync(
        Instant? since, string eventId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetCheckInsAsync(since, eventId, ct));

    public Task<IReadOnlyList<string>> GenerateDiscountCodesAsync(
        DiscountCodeSpec spec, CancellationToken ct = default) =>
        WithInner(inner => inner.GenerateDiscountCodesAsync(spec, ct));

    public Task<VoidIssuedTicketResult> VoidIssuedTicketAsync(
        string vendorTicketId, bool voidToHold, CancellationToken ct = default) =>
        WithInner(inner => inner.VoidIssuedTicketAsync(vendorTicketId, voidToHold, ct));

    public Task<VendorTicketDto> IssueTicketAsync(
        IssueTicketRequest request, CancellationToken ct = default) =>
        WithInner(inner => inner.IssueTicketAsync(request, ct));

    public Task CreateCheckInAsync(
        string vendorTicketId, Instant occurredAt, CancellationToken ct = default) =>
        WithInner(inner => inner.CreateCheckInAsync(vendorTicketId, occurredAt, ct));

    /// <summary>Drops the cached vendor event summary keyed on <paramref name="vendorEventId"/>.</summary>
    public void InvalidateEventSummary(string vendorEventId) =>
        _eventSummaries.DeleteKey(vendorEventId);

    private async Task<TResult> WithInner<TResult>(Func<ITicketVendorService, Task<TResult>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITicketVendorService>(TicketVendorServiceKeys.InnerServiceKey);
        return await action(inner);
    }

    private async Task WithInner(Func<ITicketVendorService, Task> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITicketVendorService>(TicketVendorServiceKeys.InnerServiceKey);
        await action(inner);
    }

    private sealed class EventSummaryCache(
        IServiceScopeFactory scopeFactory,
        IClock clock,
        Duration ttl,
        ILogger logger)
        : TrackedCache<string, CachedVendorEventSummary>("Tickets.VendorEventSummary", warmOnStartup: false, logger)
    {
        internal async ValueTask<VendorEventSummaryDto?> GetSummaryAsync(string eventId, CancellationToken ct)
        {
            if (TryGet(eventId, out var cached))
            {
                if (cached.ExpiresAt > clock.GetCurrentInstant())
                    return cached.Value;

                DeleteKey(eventId);
            }

            var loaded = await LoadRowAsync(eventId, ct).ConfigureAwait(false);
            if (loaded is not null) Set(eventId, loaded);
            return loaded?.Value;
        }

        protected override async ValueTask<CachedVendorEventSummary?> LoadRowAsync(string eventId, CancellationToken ct)
        {
            var summary = await WithInner(inner => inner.GetEventSummaryAsync(eventId, ct));
            return new CachedVendorEventSummary(summary, clock.GetCurrentInstant() + ttl);
        }

        private async Task<TResult> WithInner<TResult>(Func<ITicketVendorService, Task<TResult>> action)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredKeyedService<ITicketVendorService>(TicketVendorServiceKeys.InnerServiceKey);
            return await action(inner);
        }
    }

    private sealed record CachedVendorEventSummary(VendorEventSummaryDto Value, Instant ExpiresAt);
}

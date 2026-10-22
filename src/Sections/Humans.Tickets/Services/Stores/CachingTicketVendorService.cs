using Humans.Base.Caching;
using Humans.Base.Interfaces.Caching;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Tickets.Services.Stores;

/// <summary>
/// Caches only <see cref="GetEventSummaryAsync"/>. An order pull for an event evicts that event's summary, so it is
/// never older than the orders the sync last pulled.
/// </summary>
internal sealed class CachingTicketVendorService : ITicketVendorService
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
        _eventSummaries = new EventSummaryCache(
            (eventId, ct) => WithInnerAsync(inner => inner.GetEventSummaryAsync(eventId, ct)),
            clock, EventSummaryCacheTtl, logger);
    }

    public ICacheStats EventSummaryCacheStats => _eventSummaries;

    public async Task<VendorEventSummaryDto> GetEventSummaryAsync(
        string eventId, CancellationToken ct = default)
    {
        var summary = await _eventSummaries.GetSummaryAsync(eventId, ct);
        return summary ?? throw new InvalidOperationException(
            $"Vendor event summary load for '{eventId}' returned no result.");
    }

    public async Task<IReadOnlyList<VendorOrderDto>> GetOrdersAsync(
        Instant? since, string eventId, CancellationToken ct = default)
    {
        var orders = await WithInnerAsync(inner => inner.GetOrdersAsync(since, eventId, ct));
        _eventSummaries.DeleteKey(eventId);
        return orders;
    }

    public Task<IReadOnlyList<VendorTicketDto>> GetIssuedTicketsAsync(
        Instant? since, string eventId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetIssuedTicketsAsync(since, eventId, ct));

    public Task<IReadOnlyList<VendorCheckInDto>> GetCheckInsAsync(
        Instant? since, string eventId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetCheckInsAsync(since, eventId, ct));

    public Task<IReadOnlyList<string>> GenerateDiscountCodesAsync(
        DiscountCodeSpec spec, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GenerateDiscountCodesAsync(spec, ct));

    public Task<VoidIssuedTicketResult> VoidIssuedTicketAsync(
        string vendorTicketId, bool voidToHold, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.VoidIssuedTicketAsync(vendorTicketId, voidToHold, ct));

    public Task<VendorTicketDto> IssueTicketAsync(
        IssueTicketRequest request, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.IssueTicketAsync(request, ct));

    public Task CreateCheckInAsync(
        string vendorTicketId, Instant occurredAt, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.CreateCheckInAsync(vendorTicketId, occurredAt, ct));

    private async Task<TResult> WithInnerAsync<TResult>(Func<ITicketVendorService, Task<TResult>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITicketVendorService>(TicketVendorServiceKeys.InnerServiceKey);
        return await action(inner);
    }

    private async Task WithInnerAsync(Func<ITicketVendorService, Task> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITicketVendorService>(TicketVendorServiceKeys.InnerServiceKey);
        await action(inner);
    }

    private sealed class EventSummaryCache(
        Func<string, CancellationToken, Task<VendorEventSummaryDto>> loadSummary,
        IClock clock,
        Duration ttl,
        ILogger logger)
        : TrackedCache<string, CachedVendorEventSummary>("Tickets.VendorEventSummary", warmOnStartup: false, logger)
    {
        internal async ValueTask<VendorEventSummaryDto?> GetSummaryAsync(string eventId, CancellationToken ct)
        {
            var cached = await GetAsync(eventId, ct).ConfigureAwait(false);
            if (cached is not null && cached.ExpiresAt <= clock.GetCurrentInstant())
            {
                DeleteKey(eventId);
                cached = await GetAsync(eventId, ct).ConfigureAwait(false);
            }
            return cached?.Value;
        }

        protected override async ValueTask<CachedVendorEventSummary?> LoadRowAsync(string eventId, CancellationToken ct)
        {
            var summary = await loadSummary(eventId, ct);
            return new CachedVendorEventSummary(summary, clock.GetCurrentInstant() + ttl);
        }
    }

    private sealed record CachedVendorEventSummary(VendorEventSummaryDto Value, Instant ExpiresAt);
}

using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Tickets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services.Tickets;

/// <summary>
/// Singleton caching decorator for Tickets. Internally it keeps two tracked
/// slices: orders keyed by order id, and user holdings keyed by user id.
/// </summary>
public sealed class CachingTicketQueryService : ITicketService, ITicketCacheInvalidator, IHostedService
{
    public const string InnerServiceKey = "ticket-query-inner";

    private static readonly Duration UserHoldingsCacheTtl = Duration.FromMinutes(5);

    private readonly IMemoryCache _memoryCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OrdersCache _orders;
    private readonly UserHoldingsCache _userHoldings;

    public CachingTicketQueryService(
        IMemoryCache memoryCache,
        IServiceScopeFactory scopeFactory,
        IClock clock,
        ILogger<CachingTicketQueryService> logger)
    {
        _memoryCache = memoryCache;
        _scopeFactory = scopeFactory;
        _orders = new OrdersCache(
            async ct => await WithInner(inner => inner.GetTicketOrdersAsync(ct)),
            logger);
        _userHoldings = new UserHoldingsCache(scopeFactory, clock, UserHoldingsCacheTtl, logger);
    }

    public ICacheStats OrdersCacheStats => _orders;
    public ICacheStats UserHoldingsCacheStats => _userHoldings;

    public async Task<IReadOnlyList<TicketOrderInfo>> GetTicketOrdersAsync(CancellationToken ct = default)
    {
        var orders = await GetOrdersAsync(ct);
        return orders.Values.ToList();
    }

    public async Task<UserTicketHoldings> GetUserTicketHoldingsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var holdings = await _userHoldings.GetHoldingsAsync(userId, ct);
        return holdings ?? new UserTicketHoldings(0, []);
    }

    public Task<List<string>> GetAvailableTicketTypesAsync() =>
        WithInner(inner => inner.GetAvailableTicketTypesAsync());

    public Task<TicketDashboardStats> GetDashboardStatsAsync() =>
        WithInner(inner => inner.GetDashboardStatsAsync());

    public Task<BreakEvenResult> CalculateBreakEvenAsync(
        int ticketsSold, decimal grossRevenue, string currency,
        bool canAccessFinance, int fallbackTarget) =>
        WithInner(inner => inner.CalculateBreakEvenAsync(
            ticketsSold, grossRevenue, currency, canAccessFinance, fallbackTarget));

    public Task<TicketSalesAggregates> GetSalesAggregatesAsync() =>
        WithInner(inner => inner.GetSalesAggregatesAsync());

    public Task<CodeTrackingData> GetCodeTrackingDataAsync(string? search) =>
        WithInner(inner => inner.GetCodeTrackingDataAsync(search));

    public Task<OrdersPageResult> GetOrdersPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterPaymentStatus, string? filterTicketType, bool? filterMatched) =>
        WithInner(inner => inner.GetOrdersPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            filterPaymentStatus, filterTicketType, filterMatched));

    public Task<AttendeesPageResult> GetAttendeesPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterTicketType, string? filterStatus, bool? filterMatched, string? filterOrderId,
        bool filterMultipleTickets = false) =>
        WithInner(inner => inner.GetAttendeesPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            filterTicketType, filterStatus, filterMatched, filterOrderId, filterMultipleTickets));

    public Task<WhoHasntBoughtResult> GetWhoHasntBoughtAsync(
        string? search, string? filterTeam, string? filterTier, string? filterTicketStatus,
        int page, int pageSize) =>
        WithInner(inner => inner.GetWhoHasntBoughtAsync(
            search, filterTeam, filterTier, filterTicketStatus, page, pageSize));

    public Task<List<AttendeeExportRow>> GetAttendeeExportDataAsync() =>
        WithInner(inner => inner.GetAttendeeExportDataAsync());

    public Task<List<OrderExportRow>> GetOrderExportDataAsync() =>
        WithInner(inner => inner.GetOrderExportDataAsync());

    public Task<UserTicketExportData> GetUserTicketExportDataAsync(
        Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetUserTicketExportDataAsync(userId, ct));

    public Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default) =>
        WithInner(inner => inner.GetOrderDriftAsync(ct));

    public void InvalidateAfterTransfer(Guid senderUserId, Guid? receiverUserId)
    {
        _orders.Clear();
        _userHoldings.Invalidate(senderUserId);
        if (receiverUserId is { } receiver)
        {
            _userHoldings.Invalidate(receiver);
        }
    }

    public void InvalidateAfterContactImport()
    {
        _orders.Clear();
        _userHoldings.Clear();
    }

    public void InvalidateAll()
    {
        _orders.Clear();
        _userHoldings.Clear();
    }

    public void InvalidateAfterUserMerge(Guid sourceUserId, Guid targetUserId)
    {
        _orders.Clear();
        _userHoldings.Invalidate(sourceUserId);
        _userHoldings.Invalidate(targetUserId);
    }

    public void InvalidateVendorEventSummary(string vendorEventId) =>
        _memoryCache.Remove(CacheKeys.TicketEventSummary(vendorEventId));

    async Task IHostedService.StartAsync(CancellationToken ct)
    {
        await ((IHostedService)_orders).StartAsync(ct);
        await ((IHostedService)_userHoldings).StartAsync(ct);
    }

    Task IHostedService.StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task<IReadOnlyDictionary<Guid, TicketOrderInfo>> GetOrdersAsync(
        CancellationToken ct = default)
    {
        await _orders.EnsureWarmedForReadAsync(ct);
        return _orders.AsReadOnlyDictionary;
    }

    private async Task<TResult> WithInner<TResult>(Func<ITicketService, Task<TResult>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITicketService>(InnerServiceKey);
        return await action(inner);
    }

    private sealed class OrdersCache(
        Func<CancellationToken, Task<IReadOnlyList<TicketOrderInfo>>> loadOrders,
        ILogger logger)
        : TrackedCache<Guid, TicketOrderInfo>("Tickets.Orders", warmOnStartup: true, logger)
    {
        protected override async Task WarmAllAsync(CancellationToken ct)
        {
            var orders = await loadOrders(ct);
            foreach (var order in orders)
                Set(order.Id, order);
        }

        internal Task EnsureWarmedForReadAsync(CancellationToken ct) => EnsureWarmedAsync(ct);
    }

    private sealed class UserHoldingsCache(
        IServiceScopeFactory scopeFactory,
        IClock clock,
        Duration ttl,
        ILogger logger)
        : TrackedCache<Guid, CachedUserTicketHoldings>("Tickets.UserHoldings", warmOnStartup: false, logger)
    {
        internal async ValueTask<UserTicketHoldings?> GetHoldingsAsync(Guid userId, CancellationToken ct)
        {
            if (TryGet(userId, out var cached))
            {
                if (cached.ExpiresAt > clock.GetCurrentInstant())
                    return cached.Value;

                DeleteKey(userId);
            }

            var loaded = await LoadRowAsync(userId, ct).ConfigureAwait(false);
            if (loaded is not null) Set(userId, loaded);
            return loaded?.Value;
        }

        protected override async ValueTask<CachedUserTicketHoldings?> LoadRowAsync(Guid userId, CancellationToken ct)
        {
            var holdings = await WithInner(inner => inner.GetUserTicketHoldingsAsync(userId, ct));
            return new CachedUserTicketHoldings(holdings, clock.GetCurrentInstant() + ttl);
        }

        private async Task<TResult> WithInner<TResult>(Func<ITicketService, Task<TResult>> action)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredKeyedService<ITicketService>(InnerServiceKey);
            return await action(inner);
        }
    }

    private sealed record CachedUserTicketHoldings(UserTicketHoldings Value, Instant ExpiresAt);
}

using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services.Tickets;

/// <summary>
/// Singleton caching decorator for <see cref="ITicketQueryService"/>. It owns a
/// per-order projection cache plus short-lived per-user entries.
/// </summary>
public sealed class CachingTicketQueryService(
    IMemoryCache perUserCache,
    IServiceScopeFactory scopeFactory,
    ILogger<CachingTicketQueryService> logger) : ITicketQueryService, ITicketCacheInvalidator, IHostedService
{
    public const string InnerServiceKey = "ticket-query-inner";

    private static readonly TimeSpan UserPerUserCacheTtl = TimeSpan.FromMinutes(5);

    private readonly OrdersCache _orders = new(
        async ct =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredKeyedService<ITicketQueryService>(InnerServiceKey);
            return await inner.GetTicketOrderInfosAsync(ct);
        },
        logger);

    public ICacheStats OrdersCacheStats => _orders;

    public int Entries => _orders.Entries;

    public async Task<int> GetUserTicketCountAsync(Guid userId)
    {
        var cacheKey = CacheKeys.UserTicketCount(userId);
        if (perUserCache.TryGetExistingValue(cacheKey, out int cached))
            return cached;

        var count = await ComputeUserTicketCountAsync(userId);
        perUserCache.Set(cacheKey, count, UserPerUserCacheTtl);
        return count;
    }

    private async Task<int> ComputeUserTicketCountAsync(Guid userId)
    {
        var orders = await GetOrdersAsync();

        var matchedCount = orders.Values
            .SelectMany(o => o.Attendees)
            .Count(a => a.MatchedUserId == userId && IsValidOrCheckedIn(a.Status));
        if (matchedCount > 0)
            return matchedCount;

        return await WithInner(inner => inner.GetUserTicketCountAsync(userId));
    }

    public Task<HashSet<Guid>> GetUserIdsWithTicketsAsync() =>
        WithInner(inner => inner.GetUserIdsWithTicketsAsync());

    public async Task<HashSet<Guid>> GetAllMatchedUserIdsAsync()
    {
        var orders = await GetOrdersAsync();
        var ids = new HashSet<Guid>();
        foreach (var order in orders.Values)
        {
            if (order.MatchedUserId is { } orderUid) ids.Add(orderUid);
            foreach (var a in order.Attendees)
                if (a.MatchedUserId is { } attUid) ids.Add(attUid);
        }
        return ids;
    }

    public async Task<IReadOnlySet<Guid>> GetMatchedUserIdsForYearAsync(
        int year, CancellationToken ct = default)
    {
        var start = Instant.FromUtc(year, 1, 1, 0, 0);
        var end = Instant.FromUtc(year + 1, 1, 1, 0, 0);

        var orders = await GetOrdersAsync();
        var ids = new HashSet<Guid>();
        foreach (var order in orders.Values)
        {
            if (order.PurchasedAt < start || order.PurchasedAt >= end)
                continue;

            if (order.MatchedUserId is { } orderUid) ids.Add(orderUid);
            foreach (var a in order.Attendees)
                if (a.MatchedUserId is { } attUid) ids.Add(attUid);
        }
        return ids;
    }

    public async Task<IReadOnlyList<int>> GetMatchedTicketYearsAsync(CancellationToken ct = default)
    {
        var orders = await GetOrdersAsync();
        return orders.Values
            .Where(o => o.MatchedUserId.HasValue)
            .Select(o => o.PurchasedAt.InUtc().Year)
            .Distinct()
            .OrderByDescending(y => y)
            .ToList();
    }

    public async Task<bool> HasTicketAttendeeMatchAsync(Guid userId)
    {
        var orders = await GetOrdersAsync();
        foreach (var order in orders.Values)
        {
            if (order.MatchedUserId == userId) return true;
            foreach (var a in order.Attendees)
                if (a.MatchedUserId == userId) return true;
        }
        return false;
    }

    public Task<bool> HasCurrentEventTicketAsync(Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.HasCurrentEventTicketAsync(userId, ct));

    public async Task<List<UserTicketOrderSummary>> GetUserTicketOrderSummariesAsync(Guid userId)
    {
        var orders = await GetOrdersAsync();
        return orders.Values
            .Where(o => o.MatchedUserId == userId)
            .OrderByDescending(o => o.PurchasedAt)
            .Select(o => new UserTicketOrderSummary(
                o.BuyerName ?? string.Empty,
                o.PurchasedAt,
                o.Attendees.Count,
                o.TotalAmount,
                o.Currency))
            .ToList();
    }

    public async Task<UserTicketHoldings> GetUserTicketHoldingsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var cacheKey = CacheKeys.UserTicketHoldings(userId);
        if (perUserCache.TryGetExistingValue<UserTicketHoldings>(cacheKey, out var cached))
            return cached;

        var orders = await GetOrdersAsync();
        var orderCount = orders.Values.Count(o => o.MatchedUserId == userId);

        var visibleAttendees = orders.Values
            .Where(o => o.MatchedUserId == userId
                || o.Attendees.Any(a => a.MatchedUserId == userId))
            .SelectMany(o => o.Attendees.Select(a => (Order: o, Attendee: a)))
            .Where(pair => IsCurrentOwner(pair.Order, pair.Attendee, userId))
            .Select(pair => pair.Attendee)
            .OrderBy(a => a.Status == TicketAttendeeStatus.Void ? 1 : 0)
            .ThenBy(a => a.AttendeeName, StringComparer.OrdinalIgnoreCase)
            .Select(a => new UserTicketHoldingRow(
                a.Id,
                a.AttendeeName ?? string.Empty,
                a.AttendeeEmail,
                a.VendorTicketId,
                a.TicketTypeName ?? string.Empty,
                a.Status))
            .ToList();

        var holdings = new UserTicketHoldings(orderCount, visibleAttendees);
        perUserCache.Set(cacheKey, holdings, UserPerUserCacheTtl);
        return holdings;
    }

    public async Task<IReadOnlyList<Guid>> GetOpenTicketIdsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var orders = await GetOrdersAsync();
        return orders.Values
            .Where(o => o.MatchedUserId == userId
                && (o.PaymentStatus == TicketPaymentStatus.Paid
                    || o.PaymentStatus == TicketPaymentStatus.Pending))
            .Select(o => o.Id)
            .ToList();
    }

    public async Task<IReadOnlyCollection<Guid>> GetMatchedUserIdsForPaidOrdersAsync(
        CancellationToken ct = default)
    {
        var orders = await GetOrdersAsync();
        return orders.Values
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid && o.MatchedUserId.HasValue)
            .Select(o => o.MatchedUserId!.Value)
            .Distinct()
            .ToList();
    }

    public async Task<IReadOnlyList<Instant>> GetPaidOrderDatesInWindowAsync(
        Instant fromInclusive, Instant toExclusive, CancellationToken ct = default)
    {
        var orders = await GetOrdersAsync();
        return orders.Values
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid
                && o.PurchasedAt >= fromInclusive
                && o.PurchasedAt < toExclusive)
            .Select(o => o.PurchasedAt)
            .ToList();
    }

    public Task<List<string>> GetAvailableTicketTypesAsync() =>
        WithInner(inner => inner.GetAvailableTicketTypesAsync());

    public Task<TicketDashboardStats> GetDashboardStatsAsync() =>
        WithInner(inner => inner.GetDashboardStatsAsync());

    public Task<decimal> GetGrossTicketRevenueAsync() =>
        WithInner(inner => inner.GetGrossTicketRevenueAsync());

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

    public Task<Instant?> GetPostEventHoldDateAsync(CancellationToken ct = default) =>
        WithInner(inner => inner.GetPostEventHoldDateAsync(ct));

    public Task<UserTicketExportData> GetUserTicketExportDataAsync(
        Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetUserTicketExportDataAsync(userId, ct));

    public Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default) =>
        WithInner(inner => inner.GetOrderDriftAsync(ct));

    public async Task<IReadOnlyList<TicketOrderInfo>> GetTicketOrderInfosAsync(CancellationToken ct = default)
    {
        await _orders.EnsureWarmedPublicAsync(ct);
        return _orders.AsReadOnlyDictionary.Values.ToList();
    }

    public void InvalidateAfterTransfer(Guid senderUserId, Guid? receiverUserId)
    {
        _orders.Clear();
        perUserCache.Remove(CacheKeys.UserTicketCount(senderUserId));
        perUserCache.Remove(CacheKeys.UserTicketHoldings(senderUserId));
        if (receiverUserId is { } receiver)
        {
            perUserCache.Remove(CacheKeys.UserTicketCount(receiver));
            perUserCache.Remove(CacheKeys.UserTicketHoldings(receiver));
        }
    }

    public void InvalidateAfterContactImport() => _orders.Clear();

    public void InvalidateAll() => _orders.Clear();

    public void InvalidateAfterUserMerge(Guid sourceUserId, Guid targetUserId)
    {
        _orders.Clear();
        perUserCache.Remove(CacheKeys.UserTicketCount(sourceUserId));
        perUserCache.Remove(CacheKeys.UserTicketHoldings(sourceUserId));
        perUserCache.Remove(CacheKeys.UserTicketCount(targetUserId));
        perUserCache.Remove(CacheKeys.UserTicketHoldings(targetUserId));
    }

    public void InvalidateVendorEventSummary(string vendorEventId) =>
        perUserCache.Remove(CacheKeys.TicketEventSummary(vendorEventId));

    Task IHostedService.StartAsync(CancellationToken ct) =>
        ((IHostedService)_orders).StartAsync(ct);

    Task IHostedService.StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task<IReadOnlyDictionary<Guid, TicketOrderInfo>> GetOrdersAsync(
        CancellationToken ct = default)
    {
        await _orders.EnsureWarmedPublicAsync(ct);
        return _orders.AsReadOnlyDictionary;
    }

    private static bool IsValidOrCheckedIn(TicketAttendeeStatus status) =>
        status == TicketAttendeeStatus.Valid || status == TicketAttendeeStatus.CheckedIn;

    private static bool IsCurrentOwner(TicketOrderInfo order, TicketAttendeeInfo attendee, Guid userId)
    {
        if (attendee.MatchedUserId is { } matchedUid)
            return matchedUid == userId;
        return order.MatchedUserId == userId;
    }

    private async Task<TResult> WithInner<TResult>(Func<ITicketQueryService, Task<TResult>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITicketQueryService>(InnerServiceKey);
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

        public Task EnsureWarmedPublicAsync(CancellationToken ct) => EnsureWarmedAsync(ct);
    }
}

using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Tickets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Infrastructure.Services.Tickets;

/// <summary>
/// Singleton decorator for <see cref="ITicketQueryService"/>. It owns only the
/// short-lived per-user cache entries and ticket cache invalidation seam; all
/// ticket read-model queries stay on the inner application service contract.
/// </summary>
public sealed class CachingTicketQueryService(
    IMemoryCache perUserCache,
    IServiceScopeFactory scopeFactory) : ITicketQueryService, ITicketCacheInvalidator
{
    public const string InnerServiceKey = "ticket-query-inner";

    private static readonly TimeSpan UserPerUserCacheTtl = TimeSpan.FromMinutes(5);

    public async Task<int> GetUserTicketCountAsync(Guid userId)
    {
        var cacheKey = CacheKeys.UserTicketCount(userId);
        if (perUserCache.TryGetValue<int>(cacheKey, out var cached))
            return cached;

        var count = await WithInner(inner => inner.GetUserTicketCountAsync(userId));
        perUserCache.Set(cacheKey, count, UserPerUserCacheTtl);
        return count;
    }

    public async Task<UserTicketHoldings> GetUserTicketHoldingsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var cacheKey = CacheKeys.UserTicketHoldings(userId);
        if (perUserCache.TryGetValue<UserTicketHoldings>(cacheKey, out var cached) && cached is not null)
            return cached;

        var holdings = await WithInner(inner => inner.GetUserTicketHoldingsAsync(userId, ct));
        perUserCache.Set(cacheKey, holdings, UserPerUserCacheTtl);
        return holdings;
    }

    public Task<HashSet<Guid>> GetUserIdsWithTicketsAsync() =>
        WithInner(inner => inner.GetUserIdsWithTicketsAsync());

    public Task<HashSet<Guid>> GetAllMatchedUserIdsAsync() =>
        WithInner(inner => inner.GetAllMatchedUserIdsAsync());

    public Task<IReadOnlySet<Guid>> GetMatchedUserIdsForYearAsync(
        int year, CancellationToken ct = default) =>
        WithInner(inner => inner.GetMatchedUserIdsForYearAsync(year, ct));

    public Task<IReadOnlyList<int>> GetMatchedTicketYearsAsync(CancellationToken ct = default) =>
        WithInner(inner => inner.GetMatchedTicketYearsAsync(ct));

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

    public Task<List<string>> GetAvailableTicketTypesAsync() =>
        WithInner(inner => inner.GetAvailableTicketTypesAsync());

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
            filterTicketType, filterStatus, filterMatched, filterOrderId,
            filterMultipleTickets));

    public Task<WhoHasntBoughtResult> GetWhoHasntBoughtAsync(
        string? search, string? filterTeam, string? filterTier, string? filterTicketStatus,
        int page, int pageSize) =>
        WithInner(inner => inner.GetWhoHasntBoughtAsync(
            search, filterTeam, filterTier, filterTicketStatus, page, pageSize));

    public Task<List<AttendeeExportRow>> GetAttendeeExportDataAsync() =>
        WithInner(inner => inner.GetAttendeeExportDataAsync());

    public Task<List<OrderExportRow>> GetOrderExportDataAsync() =>
        WithInner(inner => inner.GetOrderExportDataAsync());

    public Task<bool> HasTicketAttendeeMatchAsync(Guid userId) =>
        WithInner(inner => inner.HasTicketAttendeeMatchAsync(userId));

    public Task<List<UserTicketOrderSummary>> GetUserTicketOrderSummariesAsync(Guid userId) =>
        WithInner(inner => inner.GetUserTicketOrderSummariesAsync(userId));

    public Task<IReadOnlyList<Guid>> GetOpenTicketIdsForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetOpenTicketIdsForUserAsync(userId, ct));

    public Task<Instant?> GetPostEventHoldDateAsync(CancellationToken ct = default) =>
        WithInner(inner => inner.GetPostEventHoldDateAsync(ct));

    public Task<bool> HasCurrentEventTicketAsync(Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.HasCurrentEventTicketAsync(userId, ct));

    public Task<UserTicketExportData> GetUserTicketExportDataAsync(
        Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetUserTicketExportDataAsync(userId, ct));

    public Task<IReadOnlyCollection<Guid>> GetMatchedUserIdsForPaidOrdersAsync(
        CancellationToken ct = default) =>
        WithInner(inner => inner.GetMatchedUserIdsForPaidOrdersAsync(ct));

    public Task<IReadOnlyList<Instant>> GetPaidOrderDatesInWindowAsync(
        Instant fromInclusive, Instant toExclusive, CancellationToken ct = default) =>
        WithInner(inner => inner.GetPaidOrderDatesInWindowAsync(fromInclusive, toExclusive, ct));

    public Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default) =>
        WithInner(inner => inner.GetOrderDriftAsync(ct));

    public void InvalidateAfterTransfer(Guid senderUserId, Guid? receiverUserId)
    {
        RemovePerUser(senderUserId);
        if (receiverUserId is { } receiver)
            RemovePerUser(receiver);
    }

    public void InvalidateAfterContactImport()
    {
    }

    public void InvalidateAll()
    {
    }

    public void InvalidateAfterUserMerge(Guid sourceUserId, Guid targetUserId)
    {
        RemovePerUser(sourceUserId);
        RemovePerUser(targetUserId);
    }

    public void InvalidateVendorEventSummary(string vendorEventId) =>
        perUserCache.Remove(CacheKeys.TicketEventSummary(vendorEventId));

    private void RemovePerUser(Guid userId)
    {
        perUserCache.Remove(CacheKeys.UserTicketCount(userId));
        perUserCache.Remove(CacheKeys.UserTicketHoldings(userId));
    }

    private async Task<TResult> WithInner<TResult>(Func<ITicketQueryService, Task<TResult>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITicketQueryService>(InnerServiceKey);
        return await action(inner);
    }
}

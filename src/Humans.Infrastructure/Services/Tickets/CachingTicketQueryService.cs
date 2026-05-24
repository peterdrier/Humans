using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.Tickets;

/// <summary>
/// Singleton caching decorator for Tickets. Internally it keeps two tracked
/// slices: orders keyed by order id, and user holdings keyed by user id.
/// </summary>
public sealed class CachingTicketQueryService : ITicketService, ITicketCacheInvalidator, IHostedService
{
    public const string InnerServiceKey = "ticket-query-inner";

    private readonly ITicketRepository _ticketRepository;
    private readonly IMemoryCache _memoryCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OrdersCache _orders;
    private readonly UserHoldingsCache _userHoldings;

    public CachingTicketQueryService(
        ITicketRepository ticketRepository,
        IMemoryCache memoryCache,
        IServiceScopeFactory scopeFactory,
        ILogger<CachingTicketQueryService> logger)
    {
        _ticketRepository = ticketRepository;
        _memoryCache = memoryCache;
        _scopeFactory = scopeFactory;
        _orders = new OrdersCache(ticketRepository, logger);
        _userHoldings = new UserHoldingsCache(_orders, ticketRepository, scopeFactory, logger);
    }

    public ICacheStats OrdersCacheStats => _orders;
    public ICacheStats UserHoldingsCacheStats => _userHoldings;

    /// <summary>Pass-through for tests that assert on the order projection entry count.</summary>
    public int Entries => _orders.Entries;

    public async Task<IReadOnlyList<TicketOrderInfo>> GetTicketOrdersAsync(CancellationToken ct = default)
    {
        var orders = await GetOrdersAsync(ct);
        var syncState = await _ticketRepository.GetSyncStateAsync(ct);
        var currentEventId = syncState?.VendorEventId;

        return orders.Values
            .Select(o => o with
            {
                IsCurrentEvent = !string.IsNullOrEmpty(currentEventId)
                    && string.Equals(o.VendorEventId, currentEventId, StringComparison.Ordinal),
            })
            .ToList();
    }

    public async Task<UserTicketHoldings> GetUserTicketHoldingsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var holdings = await _userHoldings.GetAsync(userId, ct);
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
        await _orders.EnsureWarmedPublicAsync(ct);
        return _orders.AsReadOnlyDictionary;
    }

    private async Task<TResult> WithInner<TResult>(Func<ITicketService, Task<TResult>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITicketService>(InnerServiceKey);
        return await action(inner);
    }

    private static TicketOrderInfo Project(TicketOrder o) => new(
        Id: o.Id,
        VendorOrderId: o.VendorOrderId,
        BuyerName: o.BuyerName,
        BuyerEmail: o.BuyerEmail,
        TotalAmount: o.TotalAmount,
        Currency: o.Currency,
        DiscountCode: o.DiscountCode,
        PaymentStatus: o.PaymentStatus,
        VendorEventId: o.VendorEventId,
        PurchasedAt: o.PurchasedAt,
        MatchedUserId: o.MatchedUserId,
        IsCurrentEvent: false,
        Attendees: o.Attendees.Select(a => new TicketAttendeeInfo(
            Id: a.Id,
            VendorTicketId: a.VendorTicketId,
            AttendeeName: a.AttendeeName,
            AttendeeEmail: a.AttendeeEmail,
            TicketTypeName: a.TicketTypeName,
            Price: a.Price,
            Status: a.Status,
            MatchedUserId: a.MatchedUserId)).ToList());

    private sealed class OrdersCache(ITicketRepository repository, ILogger logger)
        : TrackedCache<Guid, TicketOrderInfo>("Tickets.Orders", warmOnStartup: true, logger)
    {
        protected override async Task WarmAllAsync(CancellationToken ct)
        {
            var orders = await repository.GetAllOrdersWithAttendeesAsync(ct);
            foreach (var order in orders)
                Set(order.Id, Project(order));
        }

        public Task EnsureWarmedPublicAsync(CancellationToken ct) => EnsureWarmedAsync(ct);
    }

    private sealed class UserHoldingsCache(
        OrdersCache ordersCache,
        ITicketRepository ticketRepository,
        IServiceScopeFactory scopeFactory,
        ILogger logger)
        : TrackedCache<Guid, UserTicketHoldings>("Tickets.UserHoldings", warmOnStartup: false, logger)
    {
        protected override async ValueTask<UserTicketHoldings?> LoadRowAsync(Guid userId, CancellationToken ct)
        {
            var orders = await GetOrdersAsync(ct);
            var orderCount = orders.Values.Count(o => o.MatchedUserId == userId);
            var orderSummaries = orders.Values
                .Where(o => o.MatchedUserId == userId)
                .OrderByDescending(o => o.PurchasedAt)
                .Select(o => new UserTicketOrderSummary(
                    o.BuyerName ?? string.Empty,
                    o.PurchasedAt,
                    o.Attendees.Count,
                    o.TotalAmount,
                    o.Currency))
                .ToList();
            var openTicketOrderIds = orders.Values
                .Where(o => o.MatchedUserId == userId
                    && (o.PaymentStatus == TicketPaymentStatus.Paid
                        || o.PaymentStatus == TicketPaymentStatus.Pending))
                .Select(o => o.Id)
                .ToList();

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

            var syncState = await ticketRepository.GetSyncStateAsync(ct);
            var currentEventId = syncState?.VendorEventId;
            var hasCurrentEventTicket = !string.IsNullOrEmpty(currentEventId)
                && orders.Values.Any(o =>
                    string.Equals(o.VendorEventId, currentEventId, StringComparison.Ordinal)
                    && ((o.MatchedUserId == userId && o.PaymentStatus == TicketPaymentStatus.Paid)
                        || o.Attendees.Any(a =>
                            a.MatchedUserId == userId && IsValidOrCheckedIn(a.Status))));
            var ticketCount = await ComputeUserTicketCountAsync(userId, orders, ct);
            var innerHoldings = hasCurrentEventTicket
                ? await WithInner(inner => inner.GetUserTicketHoldingsAsync(userId, ct))
                : null;

            return new UserTicketHoldings(
                orderCount,
                visibleAttendees,
                hasCurrentEventTicket,
                ticketCount,
                innerHoldings?.PostEventHoldDate)
            {
                OrderSummaries = orderSummaries,
                OpenTicketOrderIds = openTicketOrderIds,
            };
        }

        private async Task<int> ComputeUserTicketCountAsync(
            Guid userId,
            IReadOnlyDictionary<Guid, TicketOrderInfo> orders,
            CancellationToken ct)
        {
            var matchedCount = orders.Values
                .SelectMany(o => o.Attendees)
                .Count(a => a.MatchedUserId == userId && IsValidOrCheckedIn(a.Status));
            if (matchedCount > 0)
                return matchedCount;

            var innerHoldings = await WithInner(inner => inner.GetUserTicketHoldingsAsync(userId, ct));
            return innerHoldings.TicketCount;
        }

        private async Task<IReadOnlyDictionary<Guid, TicketOrderInfo>> GetOrdersAsync(CancellationToken ct)
        {
            await ordersCache.EnsureWarmedPublicAsync(ct);
            return ordersCache.AsReadOnlyDictionary;
        }

        private async Task<TResult> WithInner<TResult>(Func<ITicketService, Task<TResult>> action)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredKeyedService<ITicketService>(InnerServiceKey);
            return await action(inner);
        }

        private static bool IsValidOrCheckedIn(TicketAttendeeStatus status) =>
            status == TicketAttendeeStatus.Valid || status == TicketAttendeeStatus.CheckedIn;

        private static bool IsCurrentOwner(TicketOrderInfo order, TicketAttendeeInfo attendee, Guid userId)
        {
            if (attendee.MatchedUserId is { } matchedUid)
                return matchedUid == userId;
            return order.MatchedUserId == userId;
        }
    }
}

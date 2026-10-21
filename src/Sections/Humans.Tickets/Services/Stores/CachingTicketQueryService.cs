using Humans.Base.Caching;
using Humans.Base.Interfaces.Caching;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;
using Humans.Tickets.Services.Dtos;

namespace Humans.Tickets.Services.Stores;

/// <summary>
/// Singleton caching decorator for Tickets. Internally it keeps two tracked
/// slices: orders keyed by order id, and user holdings keyed by user id.
/// </summary>
internal sealed class CachingTicketQueryService : ITicketManagement, ITicketCacheInvalidator, IHostedService
{
    public const string InnerServiceKey = "ticket-query-inner";

    private static readonly Duration UserHoldingsCacheTtl = Duration.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OrdersCache _orders;
    private readonly UserHoldingsCache _userHoldings;

    public CachingTicketQueryService(
        IServiceScopeFactory scopeFactory,
        IClock clock,
        ILogger<CachingTicketQueryService> logger)
    {
        _scopeFactory = scopeFactory;
        _orders = new OrdersCache(
            async ct => await WithInnerAsync(inner => inner.GetTicketOrdersAsync(ct)),
            logger);
        _userHoldings = new UserHoldingsCache(
            (userId, ct) => WithInnerAsync(inner => inner.GetUserTicketHoldingsAsync(userId, ct)),
            clock, UserHoldingsCacheTtl, logger);
    }

    public ICacheStats OrdersCacheStats => _orders;
    public ICacheStats UserHoldingsCacheStats => _userHoldings;

    public async Task<IReadOnlyList<TicketOrderInfo>> GetTicketOrdersAsync(CancellationToken ct = default)
    {
        await _orders.EnsureWarmedForReadAsync(ct);
        return _orders.AsReadOnlyDictionary.Values.ToList();
    }

    public async Task<TicketAttendeeInfo?> FindCurrentEventAttendeeByBarcodeAsync(
        string? barcode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return null;
        return TicketQueryService.FindCurrentEventAttendeeByBarcode(await GetTicketOrdersAsync(ct), barcode);
    }

    public async Task<UserTicketHoldings> GetUserTicketHoldingsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var holdings = await _userHoldings.GetHoldingsAsync(userId, ct);
        return holdings ?? new UserTicketHoldings(0, []);
    }

    // Participation is not cached here: one keyed query plus the Users snapshot per read.
    public Task<IReadOnlyList<EventParticipationInfo>> GetParticipationsAsync(Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetParticipationsAsync(userId, ct));

    public Task<IReadOnlyList<EventParticipationInfo>> GetAllParticipationsAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetAllParticipationsAsync(ct));

    public Task<List<string>> GetAvailableTicketTypesAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetAvailableTicketTypesAsync(ct));

    public Task<TicketDashboardStats> GetDashboardStatsAsync() =>
        WithInnerAsync(inner => inner.GetDashboardStatsAsync());

    public Task<BreakEvenResult> CalculateBreakEvenAsync(
        int ticketsSold, decimal grossRevenue, string currency,
        bool canAccessFinance, int fallbackTarget) =>
        WithInnerAsync(inner => inner.CalculateBreakEvenAsync(
            ticketsSold, grossRevenue, currency, canAccessFinance, fallbackTarget));

    public Task<TicketSalesAggregates> GetSalesAggregatesAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetSalesAggregatesAsync(ct));

    public Task<CodeTrackingData> GetCodeTrackingDataAsync(string? search, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetCodeTrackingDataAsync(search, ct));

    public Task<OrdersPageResult> GetOrdersPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterPaymentStatus, string? filterTicketType, bool? filterMatched, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetOrdersPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            filterPaymentStatus, filterTicketType, filterMatched, ct));

    public Task<AttendeesPageResult> GetAttendeesPageAsync(
        string? search, string sortBy, bool sortDesc,
        int page, int pageSize,
        string? filterTicketType, string? filterStatus, bool? filterMatched, string? filterOrderId,
        bool filterMultipleTickets = false, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetAttendeesPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            filterTicketType, filterStatus, filterMatched, filterOrderId, filterMultipleTickets, ct));

    public Task<WhoHasntBoughtResult> GetWhoHasntBoughtAsync(
        string? search, string? filterTeam, string? filterTier, string? filterTicketStatus,
        int page, int pageSize, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetWhoHasntBoughtAsync(
            search, filterTeam, filterTier, filterTicketStatus, page, pageSize, ct));

    public Task<List<AttendeeExportRow>> GetAttendeeExportDataAsync(Guid actorUserId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetAttendeeExportDataAsync(actorUserId, ct));

    public Task<List<OrderExportRow>> GetOrderExportDataAsync(Guid actorUserId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetOrderExportDataAsync(actorUserId, ct));

    public Task<List<DonationExportRow>> GetDonationExportDataAsync(Guid actorUserId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetDonationExportDataAsync(actorUserId, ct));

    public Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetOrderDriftAsync(ct));

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

    async Task IHostedService.StartAsync(CancellationToken ct)
    {
        await ((IHostedService)_orders).StartAsync(ct);
        await ((IHostedService)_userHoldings).StartAsync(ct);
    }

    Task IHostedService.StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task<TResult> WithInnerAsync<TResult>(Func<ITicketManagement, Task<TResult>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITicketManagement>(InnerServiceKey);
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
        Func<Guid, CancellationToken, Task<UserTicketHoldings>> loadHoldings,
        IClock clock,
        Duration ttl,
        ILogger logger)
        : TrackedCache<Guid, CachedUserTicketHoldings>("Tickets.UserHoldings", warmOnStartup: false, logger)
    {
        internal async ValueTask<UserTicketHoldings?> GetHoldingsAsync(Guid userId, CancellationToken ct)
        {
            var cached = await GetAsync(userId, ct).ConfigureAwait(false);
            if (cached is not null && cached.ExpiresAt <= clock.GetCurrentInstant())
            {
                DeleteKey(userId);
                cached = await GetAsync(userId, ct).ConfigureAwait(false);
            }
            return cached?.Value;
        }

        protected override async ValueTask<CachedUserTicketHoldings?> LoadRowAsync(Guid userId, CancellationToken ct)
        {
            var holdings = await loadHoldings(userId, ct);
            return new CachedUserTicketHoldings(holdings, clock.GetCurrentInstant() + ttl);
        }
    }

    private sealed record CachedUserTicketHoldings(UserTicketHoldings Value, Instant ExpiresAt);
}

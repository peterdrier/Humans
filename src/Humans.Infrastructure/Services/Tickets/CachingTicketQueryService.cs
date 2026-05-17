using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services.Tickets;

/// <summary>
/// Singleton caching decorator for <see cref="ITicketQueryService"/>. Composes
/// a <see cref="TrackedCache{TKey,TValue}"/> for the per-order
/// <see cref="TicketOrderInfo"/> projection (warmed at startup, refreshed
/// wholesale on every section-level invalidation event — vendor sync, transfer
/// approve, contact import apply, account-merge fold) plus an
/// <see cref="IMemoryCache"/>-backed short-TTL per-user concern
/// (<c>UserTicketCount</c> / <c>UserTicketHoldings</c>) evicted on
/// transfer/merge but deliberately left to TTL on bulk sync.
/// </summary>
/// <remarks>
/// <para>
/// Methods that previously read from short-TTL <see cref="IMemoryCache"/>
/// entries in <see cref="Humans.Application.Services.Tickets.TicketQueryService"/>
/// now answer from the in-memory projection. Methods whose results don't
/// benefit from caching (paged admin lists, exports, dashboard stats, sales
/// aggregates, sync-state probes) delegate to the inner via a keyed scope —
/// matches <c>CachingTeamService</c>'s <c>WithInner</c> pattern.
/// </para>
/// <para>
/// Implements <see cref="ITicketCacheInvalidator"/> alongside
/// <see cref="ITicketQueryService"/> so the sync job and the account-merge
/// fold can poke cache eviction through a narrow seam without taking a
/// dependency on the full 28-method query surface.
/// </para>
/// <para>
/// Composition (multiple inner caches with different shapes) forces the
/// decorator to own <see cref="IHostedService"/> directly — can't multi-inherit
/// <see cref="TrackedCache{TKey,TValue}"/>. <see cref="IHostedService.StartAsync"/>
/// drives the orders cache's startup warmup; the inner
/// <see cref="OrdersCache"/> is not registered as a hosted service itself
/// (avoiding double-warmup). Mirrors <c>CachingShiftViewService</c> post-PR
/// nobodies-collective/Humans#587.
/// </para>
/// </remarks>
public sealed class CachingTicketQueryService
    : ITicketQueryService,
      ITicketCacheInvalidator,
      IHostedService
{
    public const string InnerServiceKey = "ticket-query-inner";

    private static readonly TimeSpan UserPerUserCacheTtl = TimeSpan.FromMinutes(5);

    private readonly IMemoryCache _perUserCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OrdersCache _orders;

    /// <summary>
    /// Stats for the projection cache, surfaced on <c>/Admin/CacheStats</c>.
    /// Composition pattern: the outer decorator is not itself a
    /// <see cref="TrackedCache{TKey,TValue}"/>, so it explicitly exposes each
    /// composed cache's stats.
    /// </summary>
    public ICacheStats OrdersCacheStats => _orders;

    /// <summary>Pass-through for tests that assert on the projection's entry count.</summary>
    public int Entries => _orders.Entries;

    public CachingTicketQueryService(
        ITicketRepository ticketRepository,
        IMemoryCache perUserCache,
        IServiceScopeFactory scopeFactory,
        ILogger<CachingTicketQueryService> logger)
    {
        _perUserCache = perUserCache;
        _scopeFactory = scopeFactory;
        _orders = new OrdersCache(ticketRepository, logger);
    }

    // ==========================================================================
    // Projection-served reads (answer from the in-memory TicketOrderInfo dict)
    // ==========================================================================

    public async Task<int> GetUserTicketCountAsync(Guid userId)
    {
        var cacheKey = CacheKeys.UserTicketCount(userId);
        if (_perUserCache.TryGetExistingValue(cacheKey, out int cached))
            return cached;

        var count = await ComputeUserTicketCountAsync(userId);
        _perUserCache.Set(cacheKey, count, UserPerUserCacheTtl);
        return count;
    }

    private async Task<int> ComputeUserTicketCountAsync(Guid userId)
    {
        // Hot path: count valid/checked-in attendees matched to the user from
        // the projection. A buyer who purchased tickets for others should NOT
        // count as having a ticket themselves — match on attendees only.
        var orders = await GetOrdersAsync();

        var matchedCount = orders.Values
            .SelectMany(o => o.Attendees)
            .Count(a => a.MatchedUserId == userId && IsValidOrCheckedIn(a.Status));
        if (matchedCount > 0)
            return matchedCount;

        // Fallback (email-matching) is business logic (§5a rule 4) — delegate
        // to the inner. The _perUserCache wrapping in GetUserTicketCountAsync
        // still caches the result for subsequent calls regardless of which
        // path produced it.
        return await WithInner(inner => inner.GetUserTicketCountAsync(userId));
    }

    public async Task<HashSet<Guid>> GetUserIdsWithTicketsAsync()
    {
        var orders = await GetOrdersAsync();
        var ids = new HashSet<Guid>();
        foreach (var order in orders.Values)
        {
            foreach (var a in order.Attendees)
            {
                if (a.MatchedUserId is { } uid && IsValidOrCheckedIn(a.Status))
                    ids.Add(uid);
            }
        }
        return ids;
    }

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
        // Mirror inner contract: years in which a matched ticket *order* was
        // purchased (buyer-matched). Attendee-only matches are intentionally
        // excluded so the admin audience-segmentation year picker stays
        // consistent with TicketRepository.GetMatchedOrderYearsAsync.
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
        // Pass-through (§5a rule 4): the decision of "which event is current"
        // and the payment/attendee-status filtering are business logic owned
        // by the inner service. Caching the answer in the projection would
        // require the decorator to track the active vendor-event id and
        // reproduce the inner's filtering — both belong on the inner.
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
        if (_perUserCache.TryGetExistingValue<UserTicketHoldings>(cacheKey, out var cached))
            return cached;

        var orders = await GetOrdersAsync();
        var orderCount = orders.Values.Count(o => o.MatchedUserId == userId);

        // Attendee visibility cascade: an order's attendees are visible to its
        // buyer; an attendee's matched user always sees their own. The two
        // sets can overlap. Mirror TicketAttendeeOwnership.IsCurrentOwner:
        // matched attendee wins, falls back to the buyer for unmatched.
        var visibleAttendees = orders.Values
            .Where(o => o.MatchedUserId == userId
                || o.Attendees.Any(a => a.MatchedUserId == userId))
            .SelectMany(o => o.Attendees.Select(a => (Order: o, Attendee: a)))
            .Where(pair => IsCurrentOwner(pair.Order, pair.Attendee, userId))
            .Select(pair => pair.Attendee)
            .OrderBy(a => a.Status == TicketAttendeeStatus.Void ? 1 : 0)
            .ThenBy(a => a.AttendeeName, StringComparer.OrdinalIgnoreCase)
            .Select(a => new UserTicketHoldingRow(
                a.AttendeeName ?? string.Empty,
                a.TicketTypeName ?? string.Empty,
                a.Status))
            .ToList();

        var holdings = new UserTicketHoldings(orderCount, visibleAttendees);
        _perUserCache.Set(cacheKey, holdings, UserPerUserCacheTtl);
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

    // ==========================================================================
    // Pass-through (no caching — paged admin lists, exports, aggregates, sync
    // state probes; all delegate to the inner scoped service).
    // ==========================================================================

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

    // ==========================================================================
    // Invalidation seams (ITicketQueryService + ITicketCacheInvalidator)
    //
    // Bulk drops use TrackedCache.Clear(), which flips the warmed flag to
    // false; the next read calls EnsureWarmedAsync via GetOrdersAsync and the
    // base coalesces concurrent warmers. No bespoke lock needed — the base
    // owns the warmup semaphore.
    // ==========================================================================

    public void InvalidateAfterTransfer(Guid senderUserId, Guid? receiverUserId)
    {
        _orders.Clear();
        _perUserCache.Remove(CacheKeys.UserTicketCount(senderUserId));
        _perUserCache.Remove(CacheKeys.UserTicketHoldings(senderUserId));
        if (receiverUserId is { } receiver)
        {
            _perUserCache.Remove(CacheKeys.UserTicketCount(receiver));
            _perUserCache.Remove(CacheKeys.UserTicketHoldings(receiver));
        }
    }

    public void InvalidateAfterContactImport() => _orders.Clear();

    public void InvalidateAll() => _orders.Clear();

    public void InvalidateAfterUserMerge(Guid sourceUserId, Guid targetUserId)
    {
        _orders.Clear();
        _perUserCache.Remove(CacheKeys.UserTicketCount(sourceUserId));
        _perUserCache.Remove(CacheKeys.UserTicketHoldings(sourceUserId));
        _perUserCache.Remove(CacheKeys.UserTicketCount(targetUserId));
        _perUserCache.Remove(CacheKeys.UserTicketHoldings(targetUserId));
    }

    public void InvalidateVendorEventSummary(string vendorEventId) =>
        _perUserCache.Remove(CacheKeys.TicketEventSummary(vendorEventId));

    // ==========================================================================
    // GDPR contributor — the export shape mirrors the inner exactly. Routes
    // through the inner because Application registers the inner as the
    // IUserDataContributor (the contributor surface is one entry per section,
    // and the inner owns the export shape).
    // ==========================================================================

    // (Not implemented here — the inner TicketQueryService still implements
    // IUserDataContributor and is registered separately at DI time. See
    // TicketsSectionExtensions.)

    // ==========================================================================
    // Warmup + projection loading
    // ==========================================================================

    /// <summary>
    /// Composition forces the decorator to own <see cref="IHostedService"/>
    /// directly. Drives the inner orders cache's startup warmup; the inner
    /// is not registered as a hosted service (would double-warm).
    /// </summary>
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

    // Mirror TicketAttendeeOwnership.IsCurrentOwner against the projection
    // shape (TicketOrderInfo / TicketAttendeeInfo instead of entities).
    // Matched attendee wins; unmatched attendees fall back to the order buyer.
    private static bool IsCurrentOwner(TicketOrderInfo order, TicketAttendeeInfo attendee, Guid userId)
    {
        if (attendee.MatchedUserId is { } matchedUid)
            return matchedUid == userId;
        return order.MatchedUserId == userId;
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
        Attendees: o.Attendees.Select(a => new TicketAttendeeInfo(
            Id: a.Id,
            VendorTicketId: a.VendorTicketId,
            AttendeeName: a.AttendeeName,
            AttendeeEmail: a.AttendeeEmail,
            TicketTypeName: a.TicketTypeName,
            Price: a.Price,
            Status: a.Status,
            MatchedUserId: a.MatchedUserId)).ToList());

    // ==========================================================================
    // Inner-scope resolver
    // ==========================================================================

    private async Task<TResult> WithInner<TResult>(Func<ITicketQueryService, Task<TResult>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITicketQueryService>(InnerServiceKey);
        return await action(inner);
    }

    // ==========================================================================
    // Inner projection cache (composition).
    //
    // Private nested class because the warmup loader needs to override
    // TrackedCache.WarmAllAsync (protected virtual), and exposing the
    // protected EnsureWarmedAsync entry-point requires a subclass anyway.
    // Keeping it nested keeps the projection shape + load query co-located
    // with the decorator that owns the projection's invalidation seam.
    // ==========================================================================
    private sealed class OrdersCache : TrackedCache<Guid, TicketOrderInfo>
    {
        private readonly ITicketRepository _repository;

        public OrdersCache(ITicketRepository repository, ILogger logger)
            : base("Tickets.Orders", warmOnStartup: true, logger)
        {
            _repository = repository;
        }

        /// <summary>
        /// Bulk-loads every order with attendees, projected into
        /// <see cref="TicketOrderInfo"/>. Driven by
        /// <see cref="TrackedCache{TKey,TValue}.EnsureWarmedAsync"/> at startup
        /// and again on demand after any <c>Clear()</c> (post-write re-warm
        /// pattern). The base owns concurrency coalescing via the warm
        /// semaphore.
        /// </summary>
        protected override async Task WarmAllAsync(CancellationToken ct)
        {
            var orders = await _repository.GetAllOrdersWithAttendeesAsync(ct);
            foreach (var order in orders)
                Set(order.Id, Project(order));
        }

        /// <summary>Public seam for the outer decorator's load-all read path.</summary>
        public Task EnsureWarmedPublicAsync(CancellationToken ct) => EnsureWarmedAsync(ct);
    }
}

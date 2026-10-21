using Humans.Base.Caching;
using Humans.Base.Interfaces.Caching;
using Humans.Gdpr.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;
using Humans.Tickets.Services.Dtos;

namespace Humans.Tickets.Services.Stores;

/// <summary>
/// Singleton caching decorator for Tickets. Internally it keeps two tracked
/// slices: orders keyed by order id, and user holdings keyed by user id.
/// </summary>
/// <remarks>
/// The cached rows are written by the sync, transfer, contact-import and GDPR
/// erasure paths and by the account-merge fold, so the decorator fronts each of
/// those interfaces too: a write delegates to its keyed inner and evicts in a
/// <c>finally</c>, the original failure still reaching the caller. Nothing outside
/// this class evicts the slices.
/// </remarks>
internal sealed class CachingTicketQueryService
    : ITicketManagement, ITicketSyncService, ITicketTransferService, IAttendeeContactImportService,
        IUserDataContributor, IUserMerge, IHostedService
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

    // ── IUserDataContributor: GDPR export + erasure ─────────────────────────

    // Static table: the erasure-coverage architecture test reads it from an
    // uninitialized instance.
    public IReadOnlyDictionary<string, string?> ErasureDeclaration => TicketQueryService.Erasure;

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        WithInnerAsync(inner => inner.ContributeForUserAsync(userId, ct));

    // The order projection is warmed with no TTL and carries the buyer/attendee names
    // and emails just tombstoned; without this the erased identity keeps being served.
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        MutateAsync<ITicketManagement>(inner => inner.EraseForUserAsync(userId, ct), ClearAll);

    // ── ITicketSyncService + IUserMerge ─────────────────────────────────────

    // A later sync stage can fail after orders/attendees have committed.
    public Task<TicketSyncResult> SyncOrdersAndAttendeesAsync(CancellationToken ct = default) =>
        MutateAsync<ITicketSyncService, TicketSyncResult>(inner => inner.SyncOrdersAndAttendeesAsync(ct), ClearAll);

    public Task<bool> IsInErrorStateAsync(CancellationToken ct = default) =>
        WithInnerAsync<ITicketSyncService, bool>(inner => inner.IsInErrorStateAsync(ct));

    public Task ResetSyncStateForFullResyncAsync() =>
        WithInnerAsync<ITicketSyncService>(inner => inner.ResetSyncStateForFullResyncAsync());

    // Both users' holdings move with the fold; without the per-user eviction a merged
    // user's tickets card lags up to the holdings TTL behind it.
    public Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        MutateAsync<ITicketSyncService>(
            inner => inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct),
            () =>
            {
                _orders.Clear();
                _userHoldings.Invalidate(mergedFromUserId);
                _userHoldings.Invalidate(mergedToUserId);
            });

    // ── ITicketTransferService ──────────────────────────────────────────────

    // Pendency is stamped into holdings ("transfer pending") and decisions change the
    // order projection's TransferredToName/TransferredAt, so every transfer mutation evicts.
    public Task<TicketTransferMutationResult> CreateRequestAsync(
        TicketTransferRequestDto dto, Guid senderUserId, CancellationToken ct = default) =>
        MutateTransferAsync(inner => inner.CreateRequestAsync(dto, senderUserId, ct));

    public Task<TicketTransferMutationResult> CancelAsync(Guid transferRequestId, Guid senderUserId, CancellationToken ct = default) =>
        MutateTransferAsync(inner => inner.CancelAsync(transferRequestId, senderUserId, ct));

    public Task<TicketTransferMutationResult> ApproveAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default) =>
        MutateTransferAsync(inner => inner.ApproveAsync(transferRequestId, adminUserId, adminNotes, ct));

    public Task<TicketTransferMutationResult> ProcessTransferAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default) =>
        MutateTransferAsync(inner => inner.ProcessTransferAsync(transferRequestId, adminUserId, adminNotes, ct));

    public Task<TicketTransferMutationResult> RetryReissueAsync(
        Guid transferRequestId, Guid adminUserId, string? adminNotes, CancellationToken ct = default) =>
        MutateTransferAsync(inner => inner.RetryReissueAsync(transferRequestId, adminUserId, adminNotes, ct));

    public Task<TicketTransferMutationResult> RejectAsync(
        Guid transferRequestId, Guid adminUserId, string reason, CancellationToken ct = default) =>
        MutateTransferAsync(inner => inner.RejectAsync(transferRequestId, adminUserId, reason, ct));

    public Task<IReadOnlyList<MyAttendeeRowDto>> GetMyAttendeesAsync(Guid userId, CancellationToken ct = default) =>
        WithInnerAsync<ITicketTransferService, IReadOnlyList<MyAttendeeRowDto>>(inner => inner.GetMyAttendeesAsync(userId, ct));

    public Task<TicketTransferConfirmDto?> GetConfirmationAsync(
        Guid attendeeId, Guid receiverUserId, Guid senderUserId, CancellationToken ct = default) =>
        WithInnerAsync<ITicketTransferService, TicketTransferConfirmDto?>(
            inner => inner.GetConfirmationAsync(attendeeId, receiverUserId, senderUserId, ct));

    public Task<IReadOnlyList<TicketTransferRowDto>> GetByStatusAsync(
        TicketTransferStatus status, CancellationToken ct = default) =>
        WithInnerAsync<ITicketTransferService, IReadOnlyList<TicketTransferRowDto>>(inner => inner.GetByStatusAsync(status, ct));

    public Task<IReadOnlyList<TicketTransferRowDto>> GetBySenderAsync(Guid userId, CancellationToken ct = default) =>
        WithInnerAsync<ITicketTransferService, IReadOnlyList<TicketTransferRowDto>>(inner => inner.GetBySenderAsync(userId, ct));

    public Task<TicketTransferDetailDto?> GetDetailAsync(Guid transferRequestId, CancellationToken ct = default) =>
        WithInnerAsync<ITicketTransferService, TicketTransferDetailDto?>(inner => inner.GetDetailAsync(transferRequestId, ct));

    public Task<int> CountPendingAsync(CancellationToken ct = default) =>
        WithInnerAsync<ITicketTransferService, int>(inner => inner.CountPendingAsync(ct));

    // ── IAttendeeContactImportService ───────────────────────────────────────

    public Task<AttendeeImportPlan> BuildPlanAsync(CancellationToken ct = default) =>
        WithInnerAsync<IAttendeeContactImportService, AttendeeImportPlan>(inner => inner.BuildPlanAsync(ct));

    // Matches land on attendee rows across many users, so both slices clear.
    public Task<AttendeeImportResult> ApplyAsync(
        AttendeeImportPlan plan, IReadOnlySet<Guid> selectedAttendeeIds, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync<IAttendeeContactImportService, AttendeeImportResult>(
            inner => inner.ApplyAsync(plan, selectedAttendeeIds, actorUserId, ct), ClearAll);

    async Task IHostedService.StartAsync(CancellationToken ct)
    {
        await ((IHostedService)_orders).StartAsync(ct);
        await ((IHostedService)_userHoldings).StartAsync(ct);
    }

    Task IHostedService.StopAsync(CancellationToken ct) => Task.CompletedTask;

    // ── Inner-service plumbing ──────────────────────────────────────────────

    private void ClearAll()
    {
        _orders.Clear();
        _userHoldings.Clear();
    }

    private Task<TicketTransferMutationResult> MutateTransferAsync(
        Func<ITicketTransferService, Task<TicketTransferMutationResult>> work) =>
        MutateAsync<ITicketTransferService, TicketTransferMutationResult>(work, ClearAll);

    private Task MutateAsync<TInner>(Func<TInner, Task> work, Action evict) where TInner : notnull =>
        MutateAsync<TInner, bool>(async inner =>
        {
            await work(inner);
            return true;
        }, evict);

    // A repository write can commit before a later step fails, so eviction runs in a
    // finally and the original failure still reaches the caller.
    private async Task<TResult> MutateAsync<TInner, TResult>(Func<TInner, Task<TResult>> work, Action evict)
        where TInner : notnull
    {
        try
        {
            return await WithInnerAsync<TInner, TResult>(work);
        }
        finally
        {
            evict();
        }
    }

    private Task<TResult> WithInnerAsync<TResult>(Func<ITicketManagement, Task<TResult>> action) =>
        WithInnerAsync<ITicketManagement, TResult>(action);

    private Task WithInnerAsync<TInner>(Func<TInner, Task> action) where TInner : notnull =>
        WithInnerAsync<TInner, bool>(async inner =>
        {
            await action(inner);
            return true;
        });

    // Every inner (query, sync, transfer, contact import) is keyed under the same
    // InnerServiceKey on its own interface; the unkeyed interface resolves to this decorator.
    private async Task<TResult> WithInnerAsync<TInner, TResult>(Func<TInner, Task<TResult>> action)
        where TInner : notnull
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<TInner>(InnerServiceKey);
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

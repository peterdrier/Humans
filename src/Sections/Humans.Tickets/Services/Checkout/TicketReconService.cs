using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
using Microsoft.Extensions.Hosting;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

internal sealed class TicketReconService(
    ITicketCheckoutRepository repository,
    IEnumerable<ITicketCheckoutBackend> backends,
    IHostEnvironment environment,
    IClock clock,
    ILogger<TicketReconService> logger) : ITicketReconService
{
    private readonly IReadOnlyList<ITicketCheckoutBackend> _backends = backends.ToList();

    public IReadOnlyList<CheckoutBackendInfo> Backends =>
        _backends.Select(b => new CheckoutBackendInfo(b.Key, b.IsConfigured, b.Setup)).OrderBy(b => b.Key, StringComparer.Ordinal).ToList();

    public async Task<IReadOnlyList<CheckoutBackendFinding>?> CheckBackendAsync(
        string key, string? backendEventRef, CancellationToken ct = default) =>
        BackendFor(key) is { } backend
            ? await backend.CheckAsync(string.IsNullOrWhiteSpace(backendEventRef) ? null : backendEventRef.Trim(), ct)
            : null;

    private ITicketCheckoutBackend? BackendFor(string key) =>
        _backends.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.Ordinal));

    public async Task<IReadOnlyList<string>> GetBackendsInUseAsync(CancellationToken ct = default)
    {
        // An order keeps its backend when its sales setup moves on, so a backend with orders stays in use.
        var salesEvents = await repository.GetSalesEventsAsync(ct);
        return salesEvents.Select(e => e.BackendKey)
            .Concat((await AllOrdersAsync(salesEvents, ct)).Select(o => o.BackendKey))
            .Distinct(StringComparer.Ordinal).Where(key => BackendFor(key) is not null).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>QA, previews and dev share the backends' test accounts, so each sees the others' orders.</summary>
    private bool SharedAccount => !environment.IsProduction();

    public async Task<TicketRecon?> ReconcileBackendAsync(
        string backendKey, Instant from, Instant to, CancellationToken ct = default)
    {
        if (BackendFor(backendKey) is not { } backend)
            return null;
        if (!backend.IsConfigured)
            return TicketReconBuilder.Unreadable(backendKey, from, to, $"{backendKey} is not configured, so it cannot be read.");

        var salesEvents = await repository.GetSalesEventsAsync(ct);
        var orders = await AllOrdersAsync(salesEvents, ct);

        var backendOrders = new List<BackendOrderSummary>();
        string? eventRef = null;
        try
        {
            // Every event the backend's orders were sold under, and every event a setup sells through it now.
            var eventRefs = orders
                .Where(o => string.Equals(o.BackendKey, backendKey, StringComparison.Ordinal) && o.BackendOrderId is not null)
                .Select(o => backend.EventRefOf(o.BackendOrderId!))
                .Concat(salesEvents
                    .Where(e => string.Equals(e.BackendKey, backendKey, StringComparison.Ordinal))
                    .Select(e => string.IsNullOrWhiteSpace(e.BackendEventRef) ? null : e.BackendEventRef.Trim()))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var each in eventRefs)
            {
                eventRef = each;
                backendOrders.AddRange(await backend.ListOrdersAsync(
                    each, from - TicketReconBuilder.Margin, to + TicketReconBuilder.Margin, ct));
            }
        }
        catch (Exception ex) when (ex is TicketCheckoutException or HttpRequestException or ArgumentException
                                       or System.Text.Json.JsonException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Reconciliation could not read {Backend} event {EventRef}", backendKey, eventRef);
            return TicketReconBuilder.Unreadable(backendKey, from, to,
                $"{backendKey}{(eventRef is null ? "" : $" event {eventRef}")} could not be read: {ex.Message}");
        }

        var tickets = new List<TicketCheckoutTicket>();
        foreach (var salesEventId in orders
                     .Where(o => string.Equals(o.BackendKey, backendKey, StringComparison.Ordinal) && o.CreatedAt >= from && o.CreatedAt < to)
                     .Select(o => o.SalesEventId).Distinct())
            tickets.AddRange(await repository.GetTicketsForSalesEventAsync(salesEventId, ct));

        return TicketReconBuilder.Build(backendKey, from, to, orders, backendOrders, tickets, SharedAccount);
    }

    public async Task<TicketPaymentRecon?> ReconcilePaymentsAsync(
        string backendKey, Instant from, Instant to, CancellationToken ct = default)
    {
        if (BackendFor(backendKey) is not { IsConfigured: true } backend)
            return null;

        var orders = await AllOrdersAsync(await repository.GetSalesEventsAsync(ct), ct);
        PaymentLedger? ledger;
        IReadOnlyCollection<Guid>? returnedLater = null;
        try
        {
            ledger = await backend.GetPaymentLedgerAsync(from, to, ct);
            var now = clock.GetCurrentInstant();
            if (ledger is not null && to < now && TicketPaymentReconBuilder.AwaitsReturn(ledger, orders, backendKey)
                && await backend.GetPaymentLedgerAsync(to, now, ct) is { } later)
                returnedLater = TicketPaymentReconBuilder.Returned(later);
        }
        catch (Exception ex) when (ex is TicketCheckoutException or HttpRequestException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Payment reconciliation could not read {Backend}", backendKey);
            return TicketPaymentReconBuilder.Unreadable(backendKey, from, to,
                $"{backendKey}'s payment account could not be read: {ex.Message}");
        }

        if (ledger is null)
            return null;
        return TicketPaymentReconBuilder.Build(backendKey, from, to, ledger, orders, returnedLater, SharedAccount);
    }

    /// <summary>Three reconcile runs past expiry: by then the reconcile should have settled the order either way.</summary>
    private static readonly Duration StuckGrace = Duration.FromMinutes(15);

    public async Task<BackendLedger?> GetBackendLedgerAsync(string backendKey, CancellationToken ct = default)
    {
        var info = Backends.FirstOrDefault(b => string.Equals(b.Key, backendKey, StringComparison.Ordinal));
        if (info is null)
            return null;

        var now = clock.GetCurrentInstant();
        var orders = (await AllOrdersAsync(await repository.GetSalesEventsAsync(ct), ct))
            .Where(o => string.Equals(o.BackendKey, backendKey, StringComparison.Ordinal))
            .OrderByDescending(o => o.CreatedAt).ToList();
        var refunds = new List<TicketCheckoutRefund>();
        foreach (var salesEventId in orders.Select(o => o.SalesEventId).Distinct())
            refunds.AddRange(await repository.GetRefundsForSalesEventAsync(salesEventId, ct));
        var orderIds = orders.Select(o => o.Id).ToHashSet();

        var stuck = orders
            .Where(o => o.State is TicketCheckoutOrderState.Created or TicketCheckoutOrderState.Pending && o.ExpiresAt + StuckGrace < now)
            .ToList();
        // Reconciliation's paid side: a free order never reached the backend, so it is neither side's.
        var paid = orders
            .Where(o => o.State == TicketCheckoutOrderState.Paid && !(o.TotalMinor == 0 && o.BackendOrderId is null))
            .ToList();
        var mine = refunds.Where(r => orderIds.Contains(r.OrderId)).ToList();
        var unsettled = mine.Where(r => r.State is RefundState.Pending or RefundState.ManualRequired).OrderBy(r => r.CreatedAt).ToList();
        // A failed refund is terminal and a retry is a new row, so it matters only until a later attempt on the order goes through.
        var failed = mine
            .Where(r => r.State == RefundState.Failed
                && !mine.Any(later => later.OrderId == r.OrderId && later.CreatedAt > r.CreatedAt && later.State != RefundState.Failed))
            .OrderBy(r => r.CreatedAt).ToList();
        return new BackendLedger(info, orders, paid, stuck, unsettled, failed);
    }

    private async Task<List<TicketCheckoutOrder>> AllOrdersAsync(IReadOnlyList<TicketSalesEvent> salesEvents, CancellationToken ct)
    {
        var orders = new List<TicketCheckoutOrder>();
        foreach (var salesEvent in salesEvents)
            orders.AddRange(await repository.GetOrdersForSalesEventAsync(salesEvent.Id, ct));
        return orders;
    }
}

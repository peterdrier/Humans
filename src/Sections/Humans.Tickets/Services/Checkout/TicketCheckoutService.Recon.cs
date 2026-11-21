using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

internal sealed partial class TicketCheckoutService
{
    public async Task<IReadOnlyList<string>> GetBackendsInUseAsync(CancellationToken ct = default)
    {
        // An order keeps its backend when its sales setup moves on, so a backend with orders stays in use.
        var salesEvents = await repository.GetSalesEventsAsync(ct);
        return salesEvents.Select(e => e.BackendKey)
            .Concat((await AllOrdersAsync(salesEvents, ct)).Select(o => o.BackendKey))
            .Distinct(StringComparer.Ordinal).Where(key => BackendFor(key) is not null).Order(StringComparer.Ordinal).ToList();
    }

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

        return TicketReconBuilder.Build(backendKey, from, to, orders, backendOrders, tickets);
    }

    private async Task<List<TicketCheckoutOrder>> AllOrdersAsync(IReadOnlyList<TicketSalesEvent> salesEvents, CancellationToken ct)
    {
        var orders = new List<TicketCheckoutOrder>();
        foreach (var salesEvent in salesEvents)
            orders.AddRange(await repository.GetOrdersForSalesEventAsync(salesEvent.Id, ct));
        return orders;
    }
}

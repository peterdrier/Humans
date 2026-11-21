using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

internal sealed partial class TicketCheckoutService
{
    public async Task<IReadOnlyList<string>> GetBackendsInUseAsync(CancellationToken ct = default) =>
        (await repository.GetSalesEventsAsync(ct)).Select(e => e.BackendKey).Distinct(StringComparer.Ordinal)
            .Where(key => BackendFor(key) is not null).Order(StringComparer.Ordinal).ToList();

    public async Task<TicketRecon?> ReconcileBackendAsync(
        string backendKey, Instant from, Instant to, CancellationToken ct = default)
    {
        if (BackendFor(backendKey) is not { } backend)
            return null;
        if (!backend.IsConfigured)
            return TicketReconBuilder.Unreadable(backendKey, from, to, $"{backendKey} is not configured, so it cannot be read.");

        var salesEvents = await repository.GetSalesEventsAsync(ct);
        var orders = new List<TicketCheckoutOrder>();
        foreach (var salesEvent in salesEvents)
            orders.AddRange(await repository.GetOrdersForSalesEventAsync(salesEvent.Id, ct));

        var backendOrders = new List<BackendOrderSummary>();
        // A backend keeping no events is read once; one keeping them, once per event selling through it.
        foreach (var eventRef in salesEvents
                     .Where(e => string.Equals(e.BackendKey, backendKey, StringComparison.Ordinal))
                     .Select(e => string.IsNullOrWhiteSpace(e.BackendEventRef) ? null : e.BackendEventRef.Trim())
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                backendOrders.AddRange(await backend.ListOrdersAsync(
                    eventRef, from - TicketReconBuilder.Margin, to + TicketReconBuilder.Margin, ct));
            }
            catch (Exception ex) when (ex is TicketCheckoutException or HttpRequestException or ArgumentException
                                           or System.Text.Json.JsonException
                                           || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogWarning(ex, "Reconciliation could not read {Backend} event {EventRef}", backendKey, eventRef);
                return TicketReconBuilder.Unreadable(backendKey, from, to,
                    $"{backendKey}{(eventRef is null ? "" : $" event {eventRef}")} could not be read: {ex.Message}");
            }
        }

        var tickets = new List<TicketCheckoutTicket>();
        foreach (var salesEventId in orders
                     .Where(o => string.Equals(o.BackendKey, backendKey, StringComparison.Ordinal) && o.CreatedAt >= from && o.CreatedAt < to)
                     .Select(o => o.SalesEventId).Distinct())
            tickets.AddRange(await repository.GetTicketsForSalesEventAsync(salesEventId, ct));

        return TicketReconBuilder.Build(backendKey, from, to, orders, backendOrders, tickets);
    }
}

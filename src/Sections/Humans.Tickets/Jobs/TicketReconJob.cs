using Hangfire;
using Humans.Base.Interfaces;
using Humans.Tickets.Services.Checkout;
using NodaTime;

namespace Humans.Tickets.Jobs;

/// <summary>
/// The standing financial check: every backend in use, reconciled over the whole sale up to the start of today, so a
/// refund made late on an old order is caught too. Changes nothing; an imbalance is logged as an error for the
/// ticketing team to research on the reconciliation page.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 1800)]
internal sealed class TicketReconJob(
    ITicketCheckoutService checkout,
    IOrgTimeZone orgTimeZone,
    IClock clock,
    ILogger<TicketReconJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var salesEvents = await checkout.GetSalesEventsAsync(cancellationToken);
        if (salesEvents.Count == 0)
            return;
        // No order is older than the sales setup it was sold under.
        var from = salesEvents.Min(e => e.CreatedAt);
        var to = clock.GetCurrentInstant().InZone(orgTimeZone.Zone).Date.AtStartOfDayInZone(orgTimeZone.Zone).ToInstant();
        foreach (var key in await checkout.GetBackendsInUseAsync(cancellationToken))
        {
            TicketRecon? recon;
            try
            {
                recon = await checkout.ReconcileBackendAsync(key, from, to, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // One backend failing must not leave the others unchecked.
                logger.LogError(ex, "Ticket reconciliation of {Backend} could not run", key);
                continue;
            }

            if (recon is null)
                continue;
            if (recon.Balanced)
            {
                logger.LogInformation(
                    "Ticket reconciliation of {Backend} is balanced: {Orders} paid orders, {Tickets} tickets, {Net} net; {Listed} unpaid difference(s) listed",
                    key, recon.Humans.Orders, recon.Humans.Tickets, recon.Humans.NetMinor, recon.Problems.Count());
                continue;
            }
            if (recon.Error is not null)
            {
                logger.LogError("Ticket reconciliation of {Backend} could not run: {Error}", key, recon.Error);
                continue;
            }

            logger.LogError(
                "Ticket reconciliation of {Backend} is out of balance: Humans {HumansOrders} paid orders, {HumansTickets} tickets, " +
                "{HumansNet} net; backend {BackendOrders}, {BackendTickets}, {BackendNet}; {TicketsIssued} tickets issued; " +
                "{Problems} order(s) differ: {Issues}",
                key, recon.Humans.Orders, recon.Humans.Tickets, recon.Humans.NetMinor,
                recon.Backend.Orders, recon.Backend.Tickets, recon.Backend.NetMinor, recon.TicketsIssued,
                recon.Problems.Count(r => r.MoneyMoved),
                string.Join(", ", recon.Problems.Where(r => r.MoneyMoved).SelectMany(r => r.Issues).GroupBy(i => i).Select(g => $"{g.Key} {g.Count()}")));
        }
    }
}

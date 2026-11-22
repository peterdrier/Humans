using Hangfire;
using Humans.Base.Interfaces;
using Humans.Tickets.Services.Checkout;

namespace Humans.Tickets.Jobs;

/// <summary>
/// The evening resale refund batch: refunds each holder whose ticket sold in resale, once the new buyer's order has
/// held. Each run leaves one audit row per event with its totals.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 1800)]
internal sealed class TicketResaleRefundJob(ITicketCheckoutService checkout, ILogger<TicketResaleRefundJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var batch = await checkout.RunResaleRefundBatchAsync(cancellationToken);
        logger.LogInformation(
            "Resale refund batch started {Started} refund(s) for {Minor} minor units; {HeldBack} held back, {Waiting} waiting for the hold",
            batch.Started, batch.StartedMinor, batch.HeldBack, batch.Waiting);
    }
}

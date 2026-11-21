using Hangfire;
using Humans.Base.Interfaces;
using Humans.Tickets.Services.Checkout;

namespace Humans.Tickets.Jobs;

[DisableConcurrentExecution(timeoutInSeconds: 240)]
internal sealed class TicketCheckoutReconcileJob(
    ITicketCheckoutService checkout,
    ILogger<TicketCheckoutReconcileJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var changed = await checkout.ReconcileOpenOrdersAsync(cancellationToken);
        if (changed > 0)
            logger.LogInformation("Checkout reconcile moved {Count} order(s)", changed);
    }
}

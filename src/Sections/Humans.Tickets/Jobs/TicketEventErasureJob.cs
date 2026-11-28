using Hangfire;
using Humans.Base.Interfaces;
using Humans.Tickets.Services.Checkout;

namespace Humans.Tickets.Jobs;

/// <summary>
/// Erases each event's personal data at its ticketing platform once the post-event hold has passed. Runs after the
/// midnight account deletions, so the holders whose deletion the hold kept waiting are erased from their orders first.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 3600)]
internal sealed class TicketEventErasureJob(ITicketCheckoutService checkout, ILogger<TicketEventErasureJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var erased = await checkout.EraseHeldEventDataAsync(cancellationToken);
        if (erased > 0)
            logger.LogInformation("Erased the personal data of {Count} event(s) at their ticketing platform", erased);
    }
}

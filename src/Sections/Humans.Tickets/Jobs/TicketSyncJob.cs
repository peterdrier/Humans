using Hangfire;
using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Options;

namespace Humans.Tickets.Jobs;

[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class TicketSyncJob(
    ITicketSync syncService,
    IOptions<TicketVendorSettings> settings,
    ILogger<TicketSyncJob> logger) : IRecurringJob
{
    private readonly TicketVendorSettings _settings = settings.Value;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
        {
            logger.LogDebug("Ticket vendor not configured, skipping scheduled sync");
            return;
        }

        logger.LogInformation("Starting ticket sync job");

        try
        {
            var result = await syncService.SyncOrdersAndAttendeesAsync(cancellationToken);

            logger.LogInformation(
                "Ticket sync job completed: {Orders} orders, {Attendees} attendees synced",
                result.OrdersSynced, result.AttendeesSynced);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ticket sync job failed");
            throw;
        }
    }
}

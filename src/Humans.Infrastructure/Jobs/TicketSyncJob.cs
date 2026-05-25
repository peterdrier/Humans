using Hangfire;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Tickets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Hangfire recurring job that syncs ticket data from the vendor.
/// Runs every 15 minutes by default. Can also be triggered manually.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class TicketSyncJob(
    ITicketSyncService syncService,
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

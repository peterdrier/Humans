using Hangfire;
using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Tickets.Contracts;

/// <summary>
/// Hangfire recurring job that syncs ticket data from the vendor.
/// Runs every 15 minutes by default. Can also be triggered manually.
/// </summary>
/// <remarks>
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at G5 lane 5b-3
/// (nobodies-collective/Humans#866), following lane 5b-1's pattern. It sits under
/// <c>Contracts/</c> because Shell names the concrete type at registration and HUM0034
/// makes every other public type in a section assembly an error.
/// </remarks>
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

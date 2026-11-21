using Hangfire;
using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services;
using Microsoft.Extensions.Options;

namespace Humans.Tickets.Jobs;

// An explicit resource: the default lock key includes the method name, so ExecuteAsync and RunNowAsync would not exclude each other.
[DisableConcurrentExecution("tickets-vendor-sync", 300)]
internal sealed class TicketSyncJob(
    ITicketSyncService syncService,
    IOptions<TicketVendorSettings> settings,
    ILogger<TicketSyncJob> logger) : IRecurringJob
{
    private readonly TicketVendorSettings _settings = settings.Value;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!await syncService.IsScheduledSyncEnabledAsync(cancellationToken))
        {
            logger.LogDebug("Scheduled ticket sync is turned off in /Settings#tickets, skipping");
            return;
        }

        await RunNowAsync(cancellationToken);
    }

    /// <summary>The admin sync buttons' entry point: runs even while the scheduled sync is off.</summary>
    public async Task RunNowAsync(CancellationToken cancellationToken = default)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Ticket sync job cancelled during shutdown");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ticket sync job failed");
            throw;
        }
    }
}

using Hangfire;
using Humans.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Humans.Budget.Contracts;

/// <summary>
/// Hangfire recurring job that materializes ticket sales actuals into budget line items.
/// Runs daily at 04:30. Finds the active budget year's ticketing group and syncs completed weeks.
/// </summary>
/// <remarks>
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at G5 lane 5b-3
/// (nobodies-collective/Humans#866). Budget, not Tickets: both collaborators
/// (<see cref="ITicketingBudgetService"/>, <see cref="IBudgetServiceRead"/>) are Budget's,
/// the rows it writes are budget line items, and the job id is <c>budget-ticketing-sync</c>.
/// It sits under <c>Contracts/</c> because Shell names the concrete type at registration and
/// HUM0034 makes every other public type in a section assembly an error.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class TicketingBudgetSyncJob(
    ITicketingBudgetService ticketingBudgetService,
    IBudgetServiceRead budgetService,
    ILogger<TicketingBudgetSyncJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var activeYear = await budgetService.GetActiveYearAsync();
        if (activeYear is null)
        {
            logger.LogDebug("No active budget year, skipping ticketing budget sync");
            return;
        }

        logger.LogInformation("Starting ticketing budget sync for year {YearName}", activeYear.Name);

        try
        {
            var count = await ticketingBudgetService.SyncActualsAsync(activeYear.Id);
            logger.LogInformation("Ticketing budget sync completed: {Count} line items synced", count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ticketing budget sync failed for year {YearId}", activeYear.Id);
            throw;
        }
    }
}

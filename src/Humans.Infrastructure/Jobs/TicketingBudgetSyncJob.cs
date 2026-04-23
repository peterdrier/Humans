using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Tickets;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Hangfire recurring job that materializes ticket sales actuals into budget line items.
/// Runs daily at 04:30. Finds the active budget year's ticketing group and syncs completed weeks.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class TicketingBudgetSyncJob : IRecurringJob
{
    private readonly ITicketingBudgetService _ticketingBudgetService;
    private readonly IBudgetService _budgetService;
    private readonly ILogger<TicketingBudgetSyncJob> _logger;

    public TicketingBudgetSyncJob(
        ITicketingBudgetService ticketingBudgetService,
        IBudgetService budgetService,
        ILogger<TicketingBudgetSyncJob> logger)
    {
        _ticketingBudgetService = ticketingBudgetService;
        _budgetService = budgetService;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var activeYear = await _budgetService.GetActiveYearAsync();
        if (activeYear is null)
        {
            _logger.LogDebug("No active budget year, skipping ticketing budget sync");
            return;
        }

        _logger.LogInformation("Starting ticketing budget sync for year {YearName}", activeYear.Name);

        try
        {
            var count = await _ticketingBudgetService.SyncActualsAsync(activeYear.Id);
            _logger.LogInformation("Ticketing budget sync completed: {Count} line items synced", count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ticketing budget sync failed for year {YearId}", activeYear.Id);
            throw;
        }
    }
}

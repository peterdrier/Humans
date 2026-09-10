using Hangfire;
using Humans.Base.Interfaces;
using Humans.Budget.Contracts;
using Humans.Budget.Services;

namespace Humans.Budget.Jobs;

/// <summary>
/// Hangfire recurring job that materializes ticket sales actuals into budget line items.
/// Runs daily at 04:30. Finds the active budget year's ticketing group and syncs completed weeks.
/// </summary>
/// <remarks>
/// Public type with an internal constructor: Shell names the concrete type for Hangfire
/// scheduling while HUM0034 forbids other public types, so DI registration is a factory in
/// <c>Section.Register</c> (ruling 43), which can build it from within the assembly.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public sealed class TicketingBudgetSyncJob : IRecurringJob
{
    private readonly ITicketingBudgetService _ticketingBudgetService;
    private readonly IBudgetServiceRead _budgetService;
    private readonly ILogger<TicketingBudgetSyncJob> _logger;

    internal TicketingBudgetSyncJob(
        ITicketingBudgetService ticketingBudgetService,
        IBudgetServiceRead budgetService,
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

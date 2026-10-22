using Hangfire;
using Humans.Base.Interfaces;
using Humans.Budget.Contracts;
using Humans.Budget.Services;

namespace Humans.Budget.Jobs;

/// <summary>
/// Public with an internal constructor: Shell names the type for Hangfire, so DI registers it through a factory in
/// <c>Section.Register</c>.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public sealed class TicketingBudgetSyncJob : IRecurringJob
{
    private readonly ITicketingBudgetService _ticketingBudgetService;
    private readonly IBudgetService _budgetService;
    private readonly ILogger<TicketingBudgetSyncJob> _logger;

    internal TicketingBudgetSyncJob(
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
            var count = await _ticketingBudgetService.SyncActualsAsync(activeYear.Id, actorUserId: null);
            _logger.LogInformation("Ticketing budget sync completed: {Count} line items synced", count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ticketing budget sync failed for year {YearId}", activeYear.Id);
            throw;
        }
    }
}

using Hangfire;
using Humans.Base.Interfaces;
using Humans.Budget.Contracts;
using Humans.Budget.Services;

namespace Humans.Budget.Jobs;

[DisableConcurrentExecution(timeoutInSeconds: 300)]
internal sealed class TicketingBudgetSyncJob : IRecurringJob
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
        var activeYear = await _budgetService.GetActiveYearAsync(cancellationToken);
        if (activeYear is null)
        {
            _logger.LogDebug("No active budget year, skipping ticketing budget sync");
            return;
        }

        _logger.LogInformation("Starting ticketing budget sync for year {YearName}", activeYear.Name);

        try
        {
            var count = await _ticketingBudgetService.SyncActualsAsync(activeYear.Id, actorUserId: null, cancellationToken);
            _logger.LogInformation("Ticketing budget sync completed: {Count} line items synced", count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Ticketing budget sync cancelled for year {YearId}", activeYear.Id);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ticketing budget sync failed for year {YearId}", activeYear.Id);
            throw;
        }
    }
}

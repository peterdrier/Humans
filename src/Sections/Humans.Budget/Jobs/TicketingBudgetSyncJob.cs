using Hangfire;
using Humans.Application.Interfaces;
using Humans.Budget.Contracts;
using Humans.Budget.Services;
using Microsoft.Extensions.Logging;

namespace Humans.Budget.Jobs;

/// <summary>
/// Hangfire recurring job that materializes ticket sales actuals into budget line items.
/// Runs daily at 04:30. Finds the active budget year's ticketing group and syncs completed weeks.
/// </summary>
/// <remarks>
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at G5 lane 5b-3
/// (nobodies-collective/Humans#866). Budget, not Tickets: both collaborators
/// (<see cref="ITicketingBudgetService"/>, <see cref="IBudgetServiceRead"/>) are Budget's,
/// the rows it writes are budget line items, and the job id is <c>budget-ticketing-sync</c>.
/// It sits under <c>Jobs/</c> because Shell names the concrete type at registration and
/// HUM0034 makes every other public type in a section assembly an error. The constructor is
/// <c>internal</c> (Peter's ruling 43 made <see cref="ITicketingBudgetService"/> internal too),
/// so DI registration moved from Shell into <c>Section.Register</c>, which can build it with a
/// factory from within the assembly; Shell still names the public class for Hangfire scheduling.
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

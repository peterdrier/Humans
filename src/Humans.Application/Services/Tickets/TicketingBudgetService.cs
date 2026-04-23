using Humans.Application.DTOs;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Entities;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Application-layer implementation of <see cref="ITicketingBudgetService"/>.
/// Bridges the Tickets section (ticket_orders / ticket_attendees) into the
/// Budget section (budget_line_items / ticketing_projections). Goes through
/// <see cref="ITicketingBudgetRepository"/> for ticket reads and delegates all
/// Budget-owned mutations to <see cref="IBudgetService"/> — this type never
/// imports <c>Microsoft.EntityFrameworkCore</c>, enforced by
/// <c>Humans.Application.csproj</c>'s reference graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>Section ownership.</b> <c>TicketingBudgetService</c> is a co-owner of the
/// Tickets section, alongside <c>TicketQueryService</c> and
/// <c>TicketSyncService</c> (both pending their own §15 migrations in
/// sub-tasks #545a and #545c). It is the Tickets-owned gateway for feeding
/// completed-week sales into Budget.
/// </para>
/// <para>
/// <b>ticketing_projections ownership.</b> Remains Budget-owned per
/// design-rules §8. All projection reads/writes route through
/// <see cref="IBudgetService"/> — this service never touches that table.
/// </para>
/// </remarks>
public sealed class TicketingBudgetService : ITicketingBudgetService
{
    private readonly ITicketingBudgetRepository _ticketRepo;
    private readonly IBudgetService _budgetService;
    private readonly IClock _clock;
    private readonly ILogger<TicketingBudgetService> _logger;

    public TicketingBudgetService(
        ITicketingBudgetRepository ticketRepo,
        IBudgetService budgetService,
        IClock clock,
        ILogger<TicketingBudgetService> logger)
    {
        _ticketRepo = ticketRepo;
        _budgetService = budgetService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> SyncActualsAsync(Guid budgetYearId, CancellationToken ct = default)
    {
        try
        {
            // Read paid ticket sales through the narrow Tickets-owned repository.
            // TicketCount is already pre-computed server-side (valid + checked-in only).
            var orders = await _ticketRepo.GetPaidOrderSummariesAsync(ct);

            var today = _clock.GetCurrentInstant().InUtc().Date;
            var currentWeekMonday = GetIsoMonday(today);

            var weeklyActuals = orders
                .GroupBy(o => GetIsoMonday(o.PurchasedAt.InUtc().Date))
                .Where(g => g.Key < currentWeekMonday)
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var monday = g.Key;
                    var sunday = monday.PlusDays(6);
                    return new TicketingWeeklyActuals(
                        Monday: monday,
                        Sunday: sunday,
                        WeekLabel: FormatWeekLabel(monday, sunday),
                        TicketCount: g.Sum(o => o.TicketCount),
                        Revenue: g.Sum(o => o.TotalAmount),
                        StripeFees: g.Sum(o => o.StripeFee ?? 0m),
                        TicketTailorFees: g.Sum(o => o.ApplicationFee ?? 0m));
                })
                .ToList();

            // Delegate the BudgetLineItem / TicketingProjection mutations to
            // BudgetService, which owns those tables.
            return await _budgetService.SyncTicketingActualsAsync(budgetYearId, weeklyActuals, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync ticketing actuals for budget year {YearId}", budgetYearId);
            throw;
        }
    }

    public async Task<int> RefreshProjectionsAsync(Guid budgetYearId, CancellationToken ct = default)
    {
        try
        {
            return await _budgetService.RefreshTicketingProjectionsAsync(budgetYearId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh ticketing projections for budget year {YearId}", budgetYearId);
            throw;
        }
    }

    public Task<IReadOnlyList<TicketingWeekProjection>> GetProjectionsAsync(Guid budgetGroupId)
    {
        return _budgetService.GetTicketingProjectionEntriesAsync(budgetGroupId);
    }

    public int GetActualTicketsSold(BudgetGroup ticketingGroup)
    {
        return _budgetService.GetActualTicketsSold(ticketingGroup);
    }

    // NodaTime IsoDayOfWeek: Monday=1, Sunday=7.
    private static LocalDate GetIsoMonday(LocalDate date)
    {
        var dayOfWeek = (int)date.DayOfWeek;
        return date.PlusDays(-(dayOfWeek - 1));
    }

    private static string FormatWeekLabel(LocalDate monday, LocalDate sunday)
    {
        return $"{monday.ToString("MMM d", null)}–{sunday.ToString("MMM d", null)}";
    }
}

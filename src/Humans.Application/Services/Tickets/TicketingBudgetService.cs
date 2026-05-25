using Humans.Application.DTOs;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Entities;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Tickets;

/// <summary>
/// Tickets→Budget bridge: reads paid ticket sales, delegates BudgetLineItem/Projection writes to IBudgetService.
/// </summary>
public sealed class TicketingBudgetService(
    ITicketingBudgetRepository ticketRepo,
    IBudgetService budgetService,
    IClock clock,
    ILogger<TicketingBudgetService> logger) : ITicketingBudgetService
{
    public async Task<int> SyncActualsAsync(Guid budgetYearId, CancellationToken ct = default)
    {
        try
        {
            var orders = await ticketRepo.GetPaidOrderSummariesAsync(ct);

            var today = clock.GetCurrentInstant().InUtc().Date;
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

            return await budgetService.SyncTicketingActualsAsync(budgetYearId, weeklyActuals, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sync ticketing actuals for budget year {YearId}", budgetYearId);
            throw;
        }
    }

    public async Task<int> RefreshProjectionsAsync(Guid budgetYearId, CancellationToken ct = default)
    {
        try
        {
            return await budgetService.RefreshTicketingProjectionsAsync(budgetYearId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh ticketing projections for budget year {YearId}", budgetYearId);
            throw;
        }
    }

    public async Task<int> UpdateProjectionAndRefreshAsync(
        TicketingProjectionUpdateCommand command,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        await budgetService.UpdateTicketingProjectionAsync(
            command.BudgetGroupId,
            command.StartDate,
            command.EventDate,
            command.InitialSalesCount,
            command.DailySalesRate,
            command.AverageTicketPrice,
            command.VatRate,
            command.StripeFeePercent,
            command.StripeFeeFixed,
            command.TicketTailorFeePercent,
            actorUserId);

        return await RefreshProjectionsAsync(command.BudgetYearId, ct);
    }

    public Task<IReadOnlyList<TicketingWeekProjection>> GetProjectionsAsync(Guid budgetGroupId)
    {
        return budgetService.GetTicketingProjectionEntriesAsync(budgetGroupId);
    }

    public int GetActualTicketsSold(BudgetGroup ticketingGroup)
    {
        return budgetService.GetActualTicketsSold(ticketingGroup);
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

using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Orchestrator between ticket sales data (owned by the Tickets section) and the
/// ticketing budget group (owned by the Budget section). Queries TicketOrders —
/// which this service co-owns as part of the Tickets section — aggregates them
/// into completed ISO weeks, and delegates all BudgetLineItem / TicketingProjection
/// mutations to <see cref="IBudgetService"/>.
/// </summary>
public class TicketingBudgetService : ITicketingBudgetService
{
    private readonly HumansDbContext _dbContext;
    private readonly IBudgetService _budgetService;
    private readonly IClock _clock;
    private readonly ILogger<TicketingBudgetService> _logger;

    public TicketingBudgetService(
        HumansDbContext dbContext,
        IBudgetService budgetService,
        IClock clock,
        ILogger<TicketingBudgetService> logger)
    {
        _dbContext = dbContext;
        _budgetService = budgetService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> SyncActualsAsync(Guid budgetYearId, ClaimsPrincipal principal, CancellationToken ct = default)
    {
        try
        {
            // Read ticket sales (TicketOrders is co-owned by the Tickets section,
            // of which this service is a member) and aggregate them per ISO week.
            var orders = await _dbContext.TicketOrders
                .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid)
                .Select(o => new
                {
                    o.PurchasedAt,
                    o.TotalAmount,
                    o.StripeFee,
                    o.ApplicationFee,
                    TicketCount = o.Attendees.Count(a =>
                        a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn)
                })
                .ToListAsync(ct);

            var today = _clock.GetCurrentInstant().InUtc().Date;
            var currentWeekMonday = GetIsoMonday(today);

            var weeklyActuals = orders
                .GroupBy(o =>
                {
                    var date = o.PurchasedAt.InUtc().Date;
                    return GetIsoMonday(date);
                })
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
            return await _budgetService.SyncTicketingActualsAsync(budgetYearId, weeklyActuals, principal, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync ticketing actuals for budget year {YearId}", budgetYearId);
            throw;
        }
    }

    public async Task<int> RefreshProjectionsAsync(Guid budgetYearId, ClaimsPrincipal principal, CancellationToken ct = default)
    {
        try
        {
            return await _budgetService.RefreshTicketingProjectionsAsync(budgetYearId, principal, ct);
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

    private static LocalDate GetIsoMonday(LocalDate date)
    {
        // NodaTime IsoDayOfWeek: Monday=1, Sunday=7
        var dayOfWeek = (int)date.DayOfWeek;
        return date.PlusDays(-(dayOfWeek - 1));
    }

    private static string FormatWeekLabel(LocalDate monday, LocalDate sunday)
    {
        return $"{monday.ToString("MMM d", null)}–{sunday.ToString("MMM d", null)}";
    }
}

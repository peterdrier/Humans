using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

public class TicketingBudgetService : ITicketingBudgetService
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<TicketingBudgetService> _logger;

    // Prefix for auto-generated line item descriptions to identify them during sync
    private const string RevenuePrefix = "Week of ";
    private const string StripePrefix = "Stripe fees: ";
    private const string TtPrefix = "TT fees: ";
    private const string ProjectedPrefix = "Projected: ";

    // Spanish IVA rate applied to Stripe and TicketTailor processing fees
    private const int FeeVatRate = 21;

    public TicketingBudgetService(
        HumansDbContext dbContext,
        IClock clock,
        ILogger<TicketingBudgetService> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> SyncActualsAsync(Guid budgetYearId)
    {
        var ticketingGroup = await LoadTicketingGroupAsync(budgetYearId);
        if (ticketingGroup is null)
        {
            _logger.LogDebug("No ticketing group found for budget year {YearId}", budgetYearId);
            return 0;
        }

        // Get categories by name
        var revenueCategory = ticketingGroup.Categories.FirstOrDefault(c => string.Equals(c.Name, "Ticket Revenue", StringComparison.Ordinal));
        var feesCategory = ticketingGroup.Categories.FirstOrDefault(c => string.Equals(c.Name, "Processing Fees", StringComparison.Ordinal));

        if (revenueCategory is null || feesCategory is null)
        {
            _logger.LogWarning("Ticketing group missing expected categories for year {YearId}", budgetYearId);
            return 0;
        }

        // Get the VAT rate from the projection parameters (for setting on revenue line items)
        var projectionVatRate = ticketingGroup.TicketingProjection?.VatRate ?? 0;

        // Load all paid orders with fees
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
            .ToListAsync();

        // Group by ISO week (Mon-Sun)
        var today = _clock.GetCurrentInstant().InUtc().Date;
        var currentWeekMonday = GetIsoMonday(today);

        var weeklyData = orders
            .GroupBy(o =>
            {
                var date = o.PurchasedAt.InUtc().Date;
                return GetIsoMonday(date);
            })
            .Where(g => g.Key < currentWeekMonday) // Only completed weeks
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var monday = g.Key;
                var sunday = monday.PlusDays(6);
                return new
                {
                    Monday = monday,
                    Sunday = sunday,
                    Label = FormatWeekLabel(monday, sunday),
                    TicketCount = g.Sum(o => o.TicketCount),
                    Revenue = g.Sum(o => o.TotalAmount),
                    StripeFees = g.Sum(o => o.StripeFee ?? 0m),
                    TtFees = g.Sum(o => o.ApplicationFee ?? 0m)
                };
            })
            .ToList();

        var now = _clock.GetCurrentInstant();
        var lineItemsCreated = 0;

        foreach (var week in weeklyData)
        {
            var weekDesc = week.Label;

            // Revenue with VatRate set — existing VAT projection system handles the rest
            lineItemsCreated += UpsertLineItem(revenueCategory, $"{RevenuePrefix}{weekDesc}",
                week.Revenue, week.Monday, projectionVatRate, false, $"{week.TicketCount} tickets", now);

            // Fees (negative amounts) — both Stripe and TT charge 21% IVA on fees
            if (week.StripeFees > 0)
                lineItemsCreated += UpsertLineItem(feesCategory, $"{StripePrefix}{weekDesc}",
                    -week.StripeFees, week.Monday, FeeVatRate, false, null, now);
            if (week.TtFees > 0)
                lineItemsCreated += UpsertLineItem(feesCategory, $"{TtPrefix}{weekDesc}",
                    -week.TtFees, week.Monday, FeeVatRate, false, null, now);
        }

        // Update projection parameters from actuals (only when we have real data)
        if (weeklyData.Count > 0 && ticketingGroup.TicketingProjection is not null)
        {
            var totalRevenue = weeklyData.Sum(w => w.Revenue);
            var totalStripeFees = weeklyData.Sum(w => w.StripeFees);
            var totalTtFees = weeklyData.Sum(w => w.TtFees);
            var totalTickets = weeklyData.Sum(w => w.TicketCount);

            UpdateProjectionFromActuals(ticketingGroup.TicketingProjection,
                totalRevenue, totalStripeFees, totalTtFees, totalTickets, now);
        }

        // Materialize projections for future weeks
        lineItemsCreated += MaterializeProjections(ticketingGroup, revenueCategory, feesCategory, now);

        if (_dbContext.ChangeTracker.HasChanges())
            await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Ticketing budget sync: {Created} line items created/updated for {Weeks} actual weeks + projections",
            lineItemsCreated, weeklyData.Count);

        return lineItemsCreated;
    }

    public async Task<int> RefreshProjectionsAsync(Guid budgetYearId)
    {
        var ticketingGroup = await LoadTicketingGroupAsync(budgetYearId);
        if (ticketingGroup is null) return 0;

        var revenueCategory = ticketingGroup.Categories.FirstOrDefault(c => string.Equals(c.Name, "Ticket Revenue", StringComparison.Ordinal));
        var feesCategory = ticketingGroup.Categories.FirstOrDefault(c => string.Equals(c.Name, "Processing Fees", StringComparison.Ordinal));
        if (revenueCategory is null || feesCategory is null) return 0;

        var now = _clock.GetCurrentInstant();
        var created = MaterializeProjections(ticketingGroup, revenueCategory, feesCategory, now);

        if (_dbContext.ChangeTracker.HasChanges())
            await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Ticketing projections refreshed: {Count} line items", created);
        return created;
    }

    /// <summary>
    /// Updates projection parameters (AvgTicketPrice, StripeFeePercent, TicketTailorFeePercent)
    /// from actual order data so that future projections use real averages.
    /// </summary>
    private void UpdateProjectionFromActuals(TicketingProjection projection,
        decimal totalRevenue, decimal totalStripeFees, decimal totalTtFees, int totalTickets, Instant now)
    {
        if (totalTickets > 0)
        {
            projection.AverageTicketPrice = Math.Round(totalRevenue / totalTickets, 2);
        }

        if (totalRevenue > 0)
        {
            projection.StripeFeePercent = Math.Round(totalStripeFees / totalRevenue * 100m, 2);
            projection.TicketTailorFeePercent = Math.Round(totalTtFees / totalRevenue * 100m, 2);
        }

        projection.UpdatedAt = now;

        _logger.LogInformation(
            "Updated projection from actuals: AvgPrice={AvgPrice}, StripeFee={StripeFee}%, TtFee={TtFee}%, from {Tickets} tickets",
            projection.AverageTicketPrice, projection.StripeFeePercent, projection.TicketTailorFeePercent, totalTickets);
    }

    public int GetActualTicketsSold(BudgetGroup ticketingGroup)
    {
        var revenueCategory = ticketingGroup.Categories
            .FirstOrDefault(c => string.Equals(c.Name, "Ticket Revenue", StringComparison.Ordinal));

        if (revenueCategory is null) return 0;

        // Sum ticket counts from auto-generated (non-projected) revenue line items.
        // These are the actuals lines with notes like "187 tickets".
        var total = 0;
        foreach (var item in revenueCategory.LineItems)
        {
            if (!item.IsAutoGenerated) continue;
            if (item.Description.StartsWith(ProjectedPrefix, StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(item.Notes)) continue;

            // Notes format: "187 tickets" or "~42 tickets" (projected use ~)
            var notes = item.Notes.TrimStart('~');
            var spaceIdx = notes.IndexOf(' ', StringComparison.Ordinal);
            if (spaceIdx > 0 && int.TryParse(notes.AsSpan(0, spaceIdx), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count))
            {
                total += count;
            }
        }

        return total;
    }

    private async Task<BudgetGroup?> LoadTicketingGroupAsync(Guid budgetYearId)
    {
        return await _dbContext.BudgetGroups
            .Include(g => g.Categories)
                .ThenInclude(c => c.LineItems)
            .Include(g => g.TicketingProjection)
            .FirstOrDefaultAsync(g => g.BudgetYearId == budgetYearId && g.IsTicketingGroup);
    }

    public async Task<IReadOnlyList<TicketingWeekProjection>> GetProjectionsAsync(Guid budgetGroupId)
    {
        var group = await _dbContext.BudgetGroups
            .Include(g => g.TicketingProjection)
            .Include(g => g.Categories)
                .ThenInclude(c => c.LineItems)
            .FirstOrDefaultAsync(g => g.Id == budgetGroupId && g.IsTicketingGroup);

        if (group?.TicketingProjection is null)
            return [];

        var projection = group.TicketingProjection;

        if (projection.StartDate is null || projection.EventDate is null || projection.AverageTicketPrice == 0)
            return [];

        var today = _clock.GetCurrentInstant().InUtc().Date;
        var currentWeekMonday = GetIsoMonday(today);

        var projectionStart = currentWeekMonday > projection.StartDate.Value
            ? currentWeekMonday
            : GetIsoMonday(projection.StartDate.Value);
        var eventDate = projection.EventDate.Value;

        if (projectionStart >= eventDate)
            return [];

        var dailyRate = projection.DailySalesRate;
        var initialBurst = projection.InitialSalesCount;
        var isFirstWeek = true;

        var projections = new List<TicketingWeekProjection>();
        var weekStart = projectionStart;

        while (weekStart < eventDate)
        {
            var weekEnd = weekStart.PlusDays(6);
            if (weekEnd > eventDate) weekEnd = eventDate;

            var daysInWeek = Period.Between(weekStart, weekEnd.PlusDays(1), PeriodUnits.Days).Days;
            var weekTickets = (int)Math.Round(dailyRate * daysInWeek);
            if (isFirstWeek && projectionStart <= projection.StartDate.Value)
            {
                weekTickets += initialBurst;
                isFirstWeek = false;
            }
            else
            {
                isFirstWeek = false;
            }
            if (weekTickets <= 0) weekTickets = 1;

            var weekRevenue = weekTickets * projection.AverageTicketPrice;
            var stripeFees = weekRevenue * projection.StripeFeePercent / 100m +
                             weekTickets * projection.StripeFeeFixed;
            var ttFees = weekRevenue * projection.TicketTailorFeePercent / 100m;

            projections.Add(new TicketingWeekProjection
            {
                WeekLabel = FormatWeekLabel(weekStart, weekEnd),
                WeekStart = weekStart,
                WeekEnd = weekEnd,
                ProjectedTickets = weekTickets,
                ProjectedRevenue = Math.Round(weekRevenue, 2),
                ProjectedStripeFees = Math.Round(stripeFees, 2),
                ProjectedTtFees = Math.Round(ttFees, 2)
            });

            weekStart = weekEnd.PlusDays(1);
            // Snap to next Monday
            weekStart = GetIsoMonday(weekStart);
            if (weekStart <= weekEnd) weekStart = weekEnd.PlusDays(1);
        }

        return projections;
    }

    /// <summary>
    /// Remove old projected line items, then create new ones from current projection parameters.
    /// Returns number of items created.
    /// </summary>
    private int MaterializeProjections(BudgetGroup ticketingGroup,
        BudgetCategory revenueCategory, BudgetCategory feesCategory, Instant now)
    {
        var projection = ticketingGroup.TicketingProjection;
        if (projection is null || projection.StartDate is null || projection.EventDate is null
            || projection.AverageTicketPrice == 0)
        {
            // No projection configured — remove any stale projected items
            RemoveProjectedItems(revenueCategory, feesCategory);
            return 0;
        }

        // Remove old projected items first
        RemoveProjectedItems(revenueCategory, feesCategory);

        var today = _clock.GetCurrentInstant().InUtc().Date;
        var currentWeekMonday = GetIsoMonday(today);
        var eventDate = projection.EventDate.Value;

        var projectionStart = currentWeekMonday > projection.StartDate.Value
            ? currentWeekMonday
            : GetIsoMonday(projection.StartDate.Value);

        if (projectionStart >= eventDate) return 0;

        var dailyRate = projection.DailySalesRate;
        var initialBurst = projection.InitialSalesCount;
        var isFirstWeek = true;
        var created = 0;
        var weekStart = projectionStart;

        while (weekStart < eventDate)
        {
            var weekEnd = weekStart.PlusDays(6);
            if (weekEnd > eventDate) weekEnd = eventDate;

            var daysInWeek = Period.Between(weekStart, weekEnd.PlusDays(1), PeriodUnits.Days).Days;

            // First projected week includes initial burst if start date hasn't passed
            var weekTickets = (int)Math.Round(dailyRate * daysInWeek);
            if (isFirstWeek && projectionStart <= projection.StartDate.Value)
            {
                weekTickets += initialBurst;
                isFirstWeek = false;
            }
            else
            {
                isFirstWeek = false;
            }

            if (weekTickets <= 0) weekTickets = 1;

            var weekRevenue = weekTickets * projection.AverageTicketPrice;

            // Fees on revenue only
            var stripeFees = weekRevenue * projection.StripeFeePercent / 100m +
                             weekTickets * projection.StripeFeeFixed;
            var ttFees = weekRevenue * projection.TicketTailorFeePercent / 100m;

            var weekLabel = FormatWeekLabel(weekStart, weekEnd);

            // Revenue with VatRate — existing VAT projection system handles VAT automatically
            created += UpsertLineItem(revenueCategory, $"{ProjectedPrefix}{RevenuePrefix}{weekLabel}",
                Math.Round(weekRevenue, 2), weekStart, projection.VatRate, false, $"~{weekTickets} tickets", now);

            if (stripeFees > 0)
                created += UpsertLineItem(feesCategory, $"{ProjectedPrefix}{StripePrefix}{weekLabel}",
                    -Math.Round(stripeFees, 2), weekStart, FeeVatRate, false, null, now);
            if (ttFees > 0)
                created += UpsertLineItem(feesCategory, $"{ProjectedPrefix}{TtPrefix}{weekLabel}",
                    -Math.Round(ttFees, 2), weekStart, FeeVatRate, false, null, now);

            weekStart = weekEnd.PlusDays(1);
            weekStart = GetIsoMonday(weekStart);
            if (weekStart <= weekEnd) weekStart = weekEnd.PlusDays(1);
        }

        return created;
    }

    private void RemoveProjectedItems(params BudgetCategory[] categories)
    {
        foreach (var category in categories)
        {
            var projected = category.LineItems
                .Where(li => li.IsAutoGenerated && li.Description.StartsWith(ProjectedPrefix, StringComparison.Ordinal))
                .ToList();

            foreach (var item in projected)
            {
                category.LineItems.Remove(item);
                _dbContext.BudgetLineItems.Remove(item);
            }
        }
    }

    /// <summary>
    /// Upsert a line item by description match within a category (auto-generated items only).
    /// Returns 1 if created or updated, 0 if unchanged.
    /// </summary>
    private int UpsertLineItem(BudgetCategory category, string description, decimal amount,
        LocalDate expectedDate, int vatRate, bool isCashflowOnly, string? notes, Instant now)
    {
        var existing = category.LineItems
            .FirstOrDefault(li => li.IsAutoGenerated && string.Equals(li.Description, description, StringComparison.Ordinal));

        if (existing is not null)
        {
            // Update if values changed
            if (existing.Amount == amount && existing.VatRate == vatRate
                && string.Equals(existing.Notes, notes, StringComparison.Ordinal))
                return 0;

            existing.Amount = amount;
            existing.VatRate = vatRate;
            existing.Notes = notes;
            existing.ExpectedDate = expectedDate;
            existing.UpdatedAt = now;
            return 1;
        }

        // Create new
        var maxSort = category.LineItems.Any() ? category.LineItems.Max(li => li.SortOrder) : -1;
        var lineItem = new BudgetLineItem
        {
            Id = Guid.NewGuid(),
            BudgetCategoryId = category.Id,
            Description = description,
            Amount = amount,
            ExpectedDate = expectedDate,
            VatRate = vatRate,
            IsAutoGenerated = true,
            IsCashflowOnly = isCashflowOnly,
            Notes = notes,
            SortOrder = maxSort + 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.BudgetLineItems.Add(lineItem);
        category.LineItems.Add(lineItem);
        return 1;
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

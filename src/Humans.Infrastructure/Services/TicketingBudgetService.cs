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
    private const string VatPrefix = "VAT: ";
    private const string DonationPrefix = "Donations: ";

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
        var ticketingGroup = await _dbContext.BudgetGroups
            .Include(g => g.Categories)
                .ThenInclude(c => c.LineItems)
            .Include(g => g.TicketingProjection)
            .FirstOrDefaultAsync(g => g.BudgetYearId == budgetYearId && g.IsTicketingGroup);

        if (ticketingGroup is null)
        {
            _logger.LogDebug("No ticketing group found for budget year {YearId}", budgetYearId);
            return 0;
        }

        // Get categories by name
        var revenueCategory = ticketingGroup.Categories.FirstOrDefault(c => string.Equals(c.Name, "Ticket Revenue", StringComparison.Ordinal));
        var feesCategory = ticketingGroup.Categories.FirstOrDefault(c => string.Equals(c.Name, "Processing Fees", StringComparison.Ordinal));
        var vatCategory = ticketingGroup.Categories.FirstOrDefault(c => string.Equals(c.Name, "VAT Liability", StringComparison.Ordinal));
        var donationCategory = ticketingGroup.Categories.FirstOrDefault(c => string.Equals(c.Name, "Donations", StringComparison.Ordinal));

        if (revenueCategory is null || feesCategory is null || vatCategory is null || donationCategory is null)
        {
            _logger.LogWarning("Ticketing group missing expected categories for year {YearId}", budgetYearId);
            return 0;
        }

        // Load all paid orders with fees
        var orders = await _dbContext.TicketOrders
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid)
            .Select(o => new
            {
                o.PurchasedAt,
                o.TotalAmount,
                o.DonationAmount,
                o.VatAmount,
                o.StripeFee,
                o.ApplicationFee,
                TicketCount = o.Attendees.Count(a =>
                    a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn)
            })
            .ToListAsync();

        if (orders.Count == 0)
        {
            _logger.LogDebug("No paid orders found for ticketing budget sync");
            return 0;
        }

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
                    TtFees = g.Sum(o => o.ApplicationFee ?? 0m),
                    VatAmount = g.Sum(o => o.VatAmount),
                    Donations = g.Sum(o => o.DonationAmount)
                };
            })
            .ToList();

        var now = _clock.GetCurrentInstant();
        var lineItemsCreated = 0;

        foreach (var week in weeklyData)
        {
            var weekDesc = week.Label;

            // Quarter-boundary split: check if the week spans a quarter boundary
            var startQuarter = GetSpanishQuarter(week.Monday);
            var endQuarter = GetSpanishQuarter(week.Sunday);

            if (startQuarter != endQuarter)
            {
                // Split the week at the quarter boundary for VAT attribution
                // Revenue and fees use the full week amounts, but VAT is split proportionally
                var quarterBoundary = GetQuarterStart(endQuarter.year, endQuarter.quarter);
                var daysInQ1 = Period.Between(week.Monday, quarterBoundary).Days;
                var daysInQ2 = Period.Between(quarterBoundary, week.Sunday.PlusDays(1)).Days;
                var totalDays = daysInQ1 + daysInQ2;
                var q1Fraction = (decimal)daysInQ1 / totalDays;

                // Revenue line item for the full week (attributed to the week start)
                lineItemsCreated += UpsertLineItem(revenueCategory, $"{RevenuePrefix}{weekDesc}",
                    week.Revenue, week.Monday, 0, false, $"{week.TicketCount} tickets", now);

                // Fees for the full week
                if (week.StripeFees > 0)
                    lineItemsCreated += UpsertLineItem(feesCategory, $"{StripePrefix}{weekDesc}",
                        -week.StripeFees, week.Monday, 0, false, null, now);
                if (week.TtFees > 0)
                    lineItemsCreated += UpsertLineItem(feesCategory, $"{TtPrefix}{weekDesc}",
                        -week.TtFees, week.Monday, 0, false, null, now);

                // VAT split across quarters
                var vatQ1 = Math.Round(week.VatAmount * q1Fraction, 2);
                var vatQ2 = week.VatAmount - vatQ1;
                if (vatQ1 > 0)
                    lineItemsCreated += UpsertLineItem(vatCategory, $"{VatPrefix}{weekDesc} (Q{startQuarter.quarter})",
                        -vatQ1, week.Monday, 0, false, $"Q{startQuarter.quarter} portion", now);
                if (vatQ2 > 0)
                    lineItemsCreated += UpsertLineItem(vatCategory, $"{VatPrefix}{weekDesc} (Q{endQuarter.quarter})",
                        -vatQ2, quarterBoundary, 0, false, $"Q{endQuarter.quarter} portion", now);
            }
            else
            {
                // Normal week — no quarter split needed
                lineItemsCreated += UpsertLineItem(revenueCategory, $"{RevenuePrefix}{weekDesc}",
                    week.Revenue, week.Monday, 0, false, $"{week.TicketCount} tickets", now);

                if (week.StripeFees > 0)
                    lineItemsCreated += UpsertLineItem(feesCategory, $"{StripePrefix}{weekDesc}",
                        -week.StripeFees, week.Monday, 0, false, null, now);
                if (week.TtFees > 0)
                    lineItemsCreated += UpsertLineItem(feesCategory, $"{TtPrefix}{weekDesc}",
                        -week.TtFees, week.Monday, 0, false, null, now);
                if (week.VatAmount > 0)
                    lineItemsCreated += UpsertLineItem(vatCategory, $"{VatPrefix}{weekDesc}",
                        -week.VatAmount, week.Monday, 0, false, null, now);
            }

            // Donations are always cashflow-only
            if (week.Donations > 0)
                lineItemsCreated += UpsertLineItem(donationCategory, $"{DonationPrefix}{weekDesc}",
                    week.Donations, week.Monday, 0, true, null, now);
        }

        if (_dbContext.ChangeTracker.HasChanges())
            await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Ticketing budget sync: {Created} line items created/updated for {Weeks} weeks",
            lineItemsCreated, weeklyData.Count);

        return lineItemsCreated;
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

        // Find the last completed week with actuals to calibrate from
        var revenueCategory = group.Categories.FirstOrDefault(c => string.Equals(c.Name, "Ticket Revenue", StringComparison.Ordinal));
        var actualLineItems = revenueCategory?.LineItems
            .Where(li => li.IsAutoGenerated && li.ExpectedDate.HasValue)
            .ToList() ?? [];

        // Compute actual tickets sold so far from notes (format: "N tickets")
        var totalActualTickets = 0;
        var lastActualWeekEnd = LocalDate.MinIsoValue;
        foreach (var li in actualLineItems)
        {
            if (li.Notes is not null && li.Notes.EndsWith(" tickets", StringComparison.Ordinal))
            {
                var parts = li.Notes.Split(' ');
                if (int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var count))
                    totalActualTickets += count;
            }
            if (li.ExpectedDate.HasValue)
            {
                var weekEnd = li.ExpectedDate.Value.PlusDays(6);
                if (weekEnd > lastActualWeekEnd)
                    lastActualWeekEnd = weekEnd;
            }
        }

        // Compute observed donation rate from actuals
        var donationCategory = group.Categories.FirstOrDefault(c => string.Equals(c.Name, "Donations", StringComparison.Ordinal));
        var totalActualRevenue = actualLineItems.Sum(li => li.Amount);
        var totalActualDonations = donationCategory?.LineItems
            .Where(li => li.IsAutoGenerated).Sum(li => li.Amount) ?? 0;
        var observedDonationRate = totalActualRevenue > 0
            ? totalActualDonations / totalActualRevenue
            : 0m;

        // Project future weeks from current week to event date
        var projectionStart = currentWeekMonday > projection.StartDate.Value
            ? currentWeekMonday
            : GetIsoMonday(projection.StartDate.Value);
        var eventDate = projection.EventDate.Value;

        // If we already have actuals, use observed daily sales velocity
        var remainingTickets = projection.InitialSalesCount +
            (int)(projection.DailySalesRate * (decimal)Period.Between(projection.StartDate.Value, eventDate).Days)
            - totalActualTickets;

        if (remainingTickets <= 0)
            return [];

        var remainingDays = Period.Between(projectionStart, eventDate).Days;
        if (remainingDays <= 0)
            return [];

        var projectedDailyRate = (decimal)remainingTickets / remainingDays;

        var projections = new List<TicketingWeekProjection>();
        var weekStart = projectionStart;

        while (weekStart < eventDate)
        {
            var weekEnd = weekStart.PlusDays(6);
            if (weekEnd > eventDate) weekEnd = eventDate;

            var daysInWeek = Period.Between(weekStart, weekEnd.PlusDays(1)).Days;
            var weekTickets = (int)Math.Round(projectedDailyRate * daysInWeek);
            if (weekTickets <= 0) weekTickets = 1;

            var weekRevenue = weekTickets * projection.AverageTicketPrice;
            var weekDonations = weekRevenue * observedDonationRate;

            // Fees on revenue + donations
            var totalForFees = weekRevenue + weekDonations;
            var stripeFees = totalForFees * projection.StripeFeePercent / 100m +
                             weekTickets * projection.StripeFeeFixed;
            var ttFees = totalForFees * projection.TicketTailorFeePercent / 100m;

            // VAT on ticket revenue only (not donations) — inclusive calculation
            var vatAmount = weekRevenue * projection.VatRate / (100m + projection.VatRate);

            projections.Add(new TicketingWeekProjection
            {
                WeekLabel = FormatWeekLabel(weekStart, weekEnd),
                WeekStart = weekStart,
                WeekEnd = weekEnd,
                ProjectedTickets = weekTickets,
                ProjectedRevenue = Math.Round(weekRevenue, 2),
                ProjectedStripeFees = Math.Round(stripeFees, 2),
                ProjectedTtFees = Math.Round(ttFees, 2),
                ProjectedVat = Math.Round(vatAmount, 2),
                ProjectedDonations = Math.Round(weekDonations, 2)
            });

            weekStart = weekEnd.PlusDays(1);
            // Snap to next Monday
            weekStart = GetIsoMonday(weekStart);
            if (weekStart <= weekEnd) weekStart = weekEnd.PlusDays(1);
        }

        return projections;
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
            if (existing.Amount == amount && string.Equals(existing.Notes, notes, StringComparison.Ordinal))
                return 0;

            existing.Amount = amount;
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

    private static (int year, int quarter) GetSpanishQuarter(LocalDate date)
    {
        var quarter = (date.Month - 1) / 3 + 1;
        return (date.Year, quarter);
    }

    private static LocalDate GetQuarterStart(int year, int quarter)
    {
        return quarter switch
        {
            1 => new LocalDate(year, 1, 1),
            2 => new LocalDate(year, 4, 1),
            3 => new LocalDate(year, 7, 1),
            4 => new LocalDate(year, 10, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(quarter))
        };
    }
}

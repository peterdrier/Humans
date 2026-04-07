using Hangfire;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.TicketAdminBoardOrAdmin)]
[Route("Tickets")]
public class TicketController : HumansControllerBase
{
    private readonly IBudgetService _budgetService;
    private readonly ITicketVendorService _vendorService;
    private readonly TicketVendorSettings _settings;
    private readonly ITicketQueryService _ticketQueryService;
    private readonly ITicketSyncService _ticketSyncService;
    private readonly ILogger<TicketController> _logger;

    public TicketController(
        IBudgetService budgetService,
        ITicketVendorService vendorService,
        IOptions<TicketVendorSettings> settings,
        ITicketQueryService ticketQueryService,
        ITicketSyncService ticketSyncService,
        UserManager<User> userManager,
        ILogger<TicketController> logger)
        : base(userManager)
    {
        _budgetService = budgetService;
        _vendorService = vendorService;
        _settings = settings.Value;
        _ticketQueryService = ticketQueryService;
        _ticketSyncService = ticketSyncService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        if (!_settings.IsConfigured)
        {
            return View(new TicketDashboardViewModel { IsConfigured = false });
        }

        var stats = await _ticketQueryService.GetDashboardStatsAsync();
        var currency = stats.RecentOrders.FirstOrDefault()?.Currency ?? "EUR";
        var canAccessFinance = RoleChecks.CanAccessFinance(User);
        var breakEven = await CalculateBreakEvenAsync(stats.TicketsSold, stats.Revenue, currency, canAccessFinance);

        int totalCapacity = 0;
        try
        {
            var summary = await _vendorService.GetEventSummaryAsync(_settings.EventId);
            totalCapacity = summary?.TotalCapacity ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch event summary from vendor");
        }

        var model = new TicketDashboardViewModel
        {
            TicketsSold = stats.TicketsSold,
            TotalCapacity = totalCapacity,
            BreakEvenDetail = breakEven.Detail,
            BreakEvenTarget = breakEven.Target,
            Currency = breakEven.Currency,
            Revenue = stats.Revenue,
            AveragePrice = stats.AveragePrice,
            TicketsRemaining = totalCapacity - stats.TicketsSold,
            TotalStripeFees = stats.TotalStripeFees,
            TotalApplicationFees = stats.TotalApplicationFees,
            NetRevenue = stats.NetRevenue,
            FeesByPaymentMethod = stats.FeesByPaymentMethod.Select(f => new PaymentMethodFeeBreakdown
            {
                PaymentMethod = f.PaymentMethod,
                OrderCount = f.OrderCount,
                TotalAmount = f.TotalAmount,
                TotalStripeFees = f.TotalStripeFees,
                TotalApplicationFees = f.TotalApplicationFees,
                EffectiveRate = f.EffectiveRate,
            }).ToList(),
            DailySales = stats.DailySalesPoints.Select(d => new DailySalesPoint
            {
                Date = d.Date,
                TicketsSold = d.TicketsSold,
                RollingAverage = d.RollingAverage,
            }).ToList(),
            UnmatchedOrderCount = stats.UnmatchedOrderCount,
            SyncStatus = stats.SyncStatus,
            SyncError = stats.SyncError,
            LastSyncAt = stats.LastSyncAt,
            RecentOrders = stats.RecentOrders.Select(o => new TicketOrderSummary
            {
                Id = o.Id,
                BuyerName = o.BuyerName,
                TicketCount = o.TicketCount,
                Amount = o.Amount,
                Currency = o.Currency,
                PurchasedAt = o.PurchasedAt,
                IsMatched = o.IsMatched,
            }).ToList(),
            IsConfigured = true,
            TotalActiveVolunteers = stats.TotalActiveVolunteers,
            VolunteersWithTickets = stats.VolunteersWithTickets,
            VolunteerCoveragePercent = stats.VolunteerCoveragePercent,
        };

        return View(model);
    }

    private async Task<BreakEvenCalculation> CalculateBreakEvenAsync(int ticketsSold, decimal revenue, string currency, bool canAccessFinance)
    {
        if (ticketsSold <= 0 || revenue <= 0)
        {
            return new BreakEvenCalculation(_settings.BreakEvenTarget, null, currency);
        }

        var activeBudgetYear = await _budgetService.GetActiveYearAsync();
        if (activeBudgetYear is null)
        {
            return new BreakEvenCalculation(_settings.BreakEvenTarget, null, currency);
        }

        var visibleGroups = canAccessFinance
            ? activeBudgetYear.Groups
            : activeBudgetYear.Groups.Where(g => !g.IsRestricted).ToList();

        // Use raw expense line items (not ComputeBudgetSummary) to avoid VAT adjustments.
        // Revenue is gross, so expenses must also be gross for an apples-to-apples comparison.
        var plannedExpenses = Math.Abs(visibleGroups
            .SelectMany(g => g.Categories)
            .SelectMany(c => c.LineItems)
            .Where(li => !li.IsCashflowOnly && li.Amount < 0)
            .Sum(li => li.Amount));
        if (plannedExpenses <= 0)
        {
            return new BreakEvenCalculation(_settings.BreakEvenTarget, null, currency);
        }

        // Break-even target from current realized average revenue per ticket:
        // A = tickets sold so far, B = gross revenue so far, C = gross planned expenses
        // D = remaining expenses = C - B
        // E = average ticket price = B / A
        // F = remaining tickets = D / E
        // G = break-even target = A + F
        // Finance hover shows D / E = F tickets still to sell.
        var averageTicketPrice = revenue / ticketsSold;

        var remainingExpenses = Math.Max(0m, plannedExpenses - revenue);
        long remainingTicketsToSell = 0;
        if (remainingExpenses > 0)
        {
            var remainingTicketCount = Math.Ceiling(remainingExpenses / averageTicketPrice);
            remainingTicketsToSell = remainingTicketCount > int.MaxValue
                ? int.MaxValue
                : decimal.ToInt32(remainingTicketCount);
        }

        var breakEvenTarget = (long)ticketsSold + remainingTicketsToSell;
        var target = breakEvenTarget > int.MaxValue
            ? int.MaxValue
            : (int)breakEvenTarget;

        var detail = canAccessFinance
            ? $"{currency} {remainingExpenses:N2} remaining expenses / {currency} {averageTicketPrice:N2} per ticket = {remainingTicketsToSell:N0} tickets still to sell"
            : null;

        return new BreakEvenCalculation(target, detail, currency);
    }

    private sealed record BreakEvenCalculation(int Target, string? Detail, string Currency);

    [HttpGet("Orders")]
    public async Task<IActionResult> Orders(
        string? search, string sortBy = "date", bool sortDesc = true,
        int page = 1, int pageSize = 25,
        string? filterPaymentStatus = null, string? filterTicketType = null,
        bool? filterMatched = null)
    {
        pageSize = pageSize.ClampPageSize();

        var result = await _ticketQueryService.GetOrdersPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            filterPaymentStatus, filterTicketType, filterMatched);

        var model = new TicketOrdersViewModel
        {
            Orders = result.Rows.Select(o => new TicketOrderRow
            {
                Id = o.Id,
                VendorOrderId = o.VendorOrderId,
                PurchasedAt = o.PurchasedAt,
                BuyerName = o.BuyerName,
                BuyerEmail = o.BuyerEmail,
                AttendeeCount = o.AttendeeCount,
                TotalAmount = o.TotalAmount,
                Currency = o.Currency,
                DiscountCode = o.DiscountCode,
                DiscountAmount = o.DiscountAmount,
                DonationAmount = o.DonationAmount,
                VatAmount = o.VatAmount,
                PaymentMethod = o.PaymentMethod,
                PaymentMethodDetail = o.PaymentMethodDetail,
                StripeFee = o.StripeFee,
                ApplicationFee = o.ApplicationFee,
                PaymentStatus = o.PaymentStatus,
                VendorDashboardUrl = o.VendorDashboardUrl,
                MatchedUserId = o.MatchedUserId,
                MatchedUserName = o.MatchedUserName
            }).ToList(),
            TotalCount = result.TotalCount,
            Page = page,
            PageSize = pageSize,
            Search = search,
            SortBy = sortBy,
            SortDesc = sortDesc,
            FilterPaymentStatus = filterPaymentStatus,
            FilterTicketType = filterTicketType,
            FilterMatched = filterMatched,
            AvailableTicketTypes = await _ticketQueryService.GetAvailableTicketTypesAsync(),
        };

        return View(model);
    }

    [HttpGet("Attendees")]
    public async Task<IActionResult> Attendees(
        string? search, string sortBy = "name", bool sortDesc = false,
        int page = 1, int pageSize = 25,
        string? filterTicketType = null, string? filterStatus = null,
        bool? filterMatched = null, string? filterOrderId = null)
    {
        pageSize = pageSize.ClampPageSize();

        var result = await _ticketQueryService.GetAttendeesPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            filterTicketType, filterStatus, filterMatched, filterOrderId);

        var model = new TicketAttendeesViewModel
        {
            Attendees = result.Rows.Select(a => new TicketAttendeeRow
            {
                Id = a.Id,
                AttendeeName = a.AttendeeName,
                AttendeeEmail = a.AttendeeEmail,
                TicketTypeName = a.TicketTypeName,
                Price = a.Price,
                IsVip = a.Price > Domain.Constants.TicketConstants.VipThresholdEuros,
                TaxableAmount = a.Price > Domain.Constants.TicketConstants.VipThresholdEuros
                    ? Domain.Constants.TicketConstants.VipThresholdEuros
                    : a.Price,
                VipDonation = a.Price > Domain.Constants.TicketConstants.VipThresholdEuros
                    ? a.Price - Domain.Constants.TicketConstants.VipThresholdEuros
                    : 0m,
                Status = a.Status,
                MatchedUserId = a.MatchedUserId,
                MatchedUserName = a.MatchedUserName,
                VendorOrderId = a.VendorOrderId
            }).ToList(),
            TotalCount = result.TotalCount,
            Page = page,
            PageSize = pageSize,
            Search = search,
            SortBy = sortBy,
            SortDesc = sortDesc,
            FilterTicketType = filterTicketType,
            FilterStatus = filterStatus,
            FilterMatched = filterMatched,
            FilterOrderId = filterOrderId,
            AvailableTicketTypes = await _ticketQueryService.GetAvailableTicketTypesAsync(),
        };

        return View(model);
    }

    [HttpGet("Codes")]
    public async Task<IActionResult> Codes(string? search)
    {
        var data = await _ticketQueryService.GetCodeTrackingDataAsync(search);

        var model = new TicketCodeTrackingViewModel
        {
            TotalCodesSent = data.TotalCodesSent,
            CodesRedeemed = data.CodesRedeemed,
            CodesUnused = data.CodesUnused,
            RedemptionRate = data.RedemptionRate,
            Campaigns = data.Campaigns.Select(c => new CampaignCodeSummary
            {
                CampaignId = c.CampaignId,
                CampaignTitle = c.CampaignTitle,
                TotalGrants = c.TotalGrants,
                Redeemed = c.Redeemed,
                Unused = c.Unused,
                RedemptionRate = c.RedemptionRate,
            }).ToList(),
            Codes = data.Codes.Select(c => new CodeDetailRow
            {
                Code = c.Code,
                RecipientName = c.RecipientName,
                RecipientUserId = c.RecipientUserId,
                CampaignTitle = c.CampaignTitle,
                Status = c.Status,
                RedeemedAt = c.RedeemedAt,
                RedeemedByName = c.RedeemedByName,
                RedeemedByEmail = c.RedeemedByEmail,
                RedeemedOrderVendorId = c.RedeemedOrderVendorId,
            }).ToList(),
            Search = search,
        };

        return View(model);
    }

    [HttpGet("GateList")]
    public IActionResult GateList()
    {
        return View();
    }

    [HttpGet("WhoHasntBought")]
    public async Task<IActionResult> WhoHasntBought(
        string? search, string? filterTeam = null, string? filterTier = null,
        string? filterTicketStatus = null,
        int page = 1, int pageSize = 25)
    {
        pageSize = pageSize.ClampPageSize();

        var result = await _ticketQueryService.GetWhoHasntBoughtAsync(
            search, filterTeam, filterTier, filterTicketStatus, page, pageSize);

        var model = new WhoHasntBoughtViewModel
        {
            Humans = result.Humans.Select(h => new WhoHasntBoughtRow
            {
                UserId = h.UserId,
                HasTicket = h.HasTicket,
                Name = h.Name,
                Email = h.Email,
                Teams = h.Teams,
                Tier = h.Tier,
            }).ToList(),
            TotalCount = result.TotalCount,
            Page = page,
            PageSize = pageSize,
            Search = search,
            FilterTeam = filterTeam,
            FilterTier = filterTier,
            FilterTicketStatus = filterTicketStatus,
            AvailableTeams = result.AvailableTeams,
        };

        return View(model);
    }

    [HttpGet("SalesAggregates")]
    public async Task<IActionResult> SalesAggregates()
    {
        var aggregates = await _ticketQueryService.GetSalesAggregatesAsync();

        var model = new TicketSalesAggregatesViewModel
        {
            WeeklySales = aggregates.WeeklySales.Select(w => new WeeklySalesRow
            {
                WeekLabel = w.WeekLabel,
                TicketsSold = w.TicketsSold,
                GrossRevenue = w.GrossRevenue,
                OrderCount = w.OrderCount,
                Donations = w.Donations,
                VatAmount = w.VatAmount,
                VipDonations = w.VipDonations,
            }).ToList(),
            QuarterlySales = aggregates.QuarterlySales.Select(q => new QuarterlySalesRow
            {
                QuarterLabel = q.QuarterLabel,
                Year = q.Year,
                Quarter = q.Quarter,
                TicketsSold = q.TicketsSold,
                GrossRevenue = q.GrossRevenue,
                OrderCount = q.OrderCount,
                Donations = q.Donations,
                VatAmount = q.VatAmount,
                VipDonations = q.VipDonations,
            }).ToList(),
        };

        return View(model);
    }

    [HttpPost("Sync")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
    public IActionResult Sync()
    {
        BackgroundJob.Enqueue<TicketSyncJob>(job => job.ExecuteAsync(CancellationToken.None));
        SetSuccess("Ticket sync triggered. Data will update shortly.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("FullResync")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> FullResync()
    {
        await _ticketSyncService.ResetSyncStateForFullResyncAsync();

        BackgroundJob.Enqueue<TicketSyncJob>(job => job.ExecuteAsync(CancellationToken.None));
        SetSuccess("Full re-sync triggered. All orders will be re-fetched.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Export/Attendees")]
    [Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
    public async Task<IActionResult> ExportAttendees()
    {
        var rows = await _ticketQueryService.GetAttendeeExportDataAsync();

        var csv = new System.Text.StringBuilder();
        csv.AppendCsvRow("Name", "Email", "Ticket Type", "Price", "Status", "Order ID");
        foreach (var a in rows)
        {
            csv.AppendCsvRow(a.AttendeeName, a.AttendeeEmail, a.TicketTypeName, a.Price, a.Status, a.VendorOrderId);
        }

        return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv", "attendees-export.csv");
    }

    [HttpGet("Export/Orders")]
    [Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
    public async Task<IActionResult> ExportOrders()
    {
        var rows = await _ticketQueryService.GetOrderExportDataAsync();

        var csv = new System.Text.StringBuilder();
        csv.AppendCsvRow("Date", "Purchaser", "Email", "Tickets", "Amount", "Currency",
            "Code", "Discount", "Donation", "VAT", "Payment Method", "Stripe Fee", "TT Fee", "Status");
        foreach (var o in rows)
        {
            csv.AppendCsvRow(
                o.Date,
                o.BuyerName,
                o.BuyerEmail,
                o.AttendeeCount,
                o.TotalAmount,
                o.Currency,
                o.DiscountCode,
                o.DiscountAmount,
                o.DonationAmount,
                o.VatAmount,
                o.PaymentMethod,
                o.StripeFee,
                o.ApplicationFee,
                o.PaymentStatus);
        }

        return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv", "orders-export.csv");
    }
}

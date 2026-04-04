using Hangfire;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.TicketAdminBoardOrAdmin)]
[Route("Tickets")]
public class TicketController : HumansControllerBase
{
    private readonly ITicketVendorService _vendorService;
    private readonly TicketVendorSettings _settings;
    private readonly ITicketQueryService _ticketQueryService;
    private readonly ITicketSyncService _ticketSyncService;
    private readonly ILogger<TicketController> _logger;

    public TicketController(
        ITicketVendorService vendorService,
        IOptions<TicketVendorSettings> settings,
        ITicketQueryService ticketQueryService,
        ITicketSyncService ticketSyncService,
        UserManager<User> userManager,
        ILogger<TicketController> logger)
        : base(userManager)
    {
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
            BreakEvenTarget = _settings.BreakEvenTarget,
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
    [Authorize(Roles = RoleGroups.TicketAdminOrAdmin)]
    public IActionResult Sync()
    {
        BackgroundJob.Enqueue<TicketSyncJob>(job => job.ExecuteAsync(CancellationToken.None));
        SetSuccess("Ticket sync triggered. Data will update shortly.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("FullResync")]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = RoleNames.Admin)]
    public async Task<IActionResult> FullResync()
    {
        await _ticketSyncService.ResetSyncStateForFullResyncAsync();

        BackgroundJob.Enqueue<TicketSyncJob>(job => job.ExecuteAsync(CancellationToken.None));
        SetSuccess("Full re-sync triggered. All orders will be re-fetched.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Export/Attendees")]
    [Authorize(Roles = RoleGroups.TicketAdminOrAdmin)]
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
    [Authorize(Roles = RoleGroups.TicketAdminOrAdmin)]
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

using Hangfire;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Infrastructure.Jobs;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Humans.Web.Models.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.TicketAdminBoardOrAdmin)]
[Route("Tickets")]
public class TicketController(
    ITicketService ticketQueryService,
    ITicketSyncService ticketSyncService,
    IUserParticipationBackfillService participationBackfillService,
    TicketDashboardPageBuilder dashboardPageBuilder,
    IUserServiceRead userService,
    ILogger<TicketController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        return View(await dashboardPageBuilder.BuildAsync(RoleChecks.CanAccessFinance(User)));
    }

    [HttpGet("Orders")]
    public async Task<IActionResult> Orders(
        string? search, string sortBy = "date", bool sortDesc = true,
        int page = 1, int pageSize = 25,
        string? filterPaymentStatus = null, string? filterTicketType = null,
        bool? filterMatched = null)
    {
        pageSize = pageSize.ClampPageSize();

        var result = await ticketQueryService.GetOrdersPageAsync(
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
            AvailableTicketTypes = (await ticketQueryService.GetAvailableTicketTypesAsync())
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList(),
        };

        return View(model);
    }

    [HttpGet("Attendees")]
    public async Task<IActionResult> Attendees(
        string? search, string sortBy = "name", bool sortDesc = false,
        int page = 1, int pageSize = 25,
        string? filterTicketType = null, string? filterStatus = null,
        bool? filterMatched = null, string? filterOrderId = null,
        bool filterMultipleTickets = false)
    {
        pageSize = pageSize.ClampPageSize();

        var result = await ticketQueryService.GetAttendeesPageAsync(
            search, sortBy, sortDesc, page, pageSize,
            filterTicketType, filterStatus, filterMatched, filterOrderId, filterMultipleTickets);

        var model = new TicketAttendeesViewModel
        {
            Attendees = result.Rows.Select(a => new TicketAttendeeRow
            {
                Id = a.Id,
                AttendeeName = a.AttendeeName,
                AttendeeEmail = a.AttendeeEmail,
                TicketTypeName = a.TicketTypeName,
                Price = a.Price,
                IsVip = a.Price > TicketConstants.VipThresholdEuros,
                TaxableAmount = a.Price > TicketConstants.VipThresholdEuros
                    ? TicketConstants.VipThresholdEuros
                    : a.Price,
                VipDonation = a.Price > TicketConstants.VipThresholdEuros
                    ? a.Price - TicketConstants.VipThresholdEuros
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
            FilterMultipleTickets = filterMultipleTickets,
            AvailableTicketTypes = (await ticketQueryService.GetAvailableTicketTypesAsync())
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList(),
        };

        return View(model);
    }

    [HttpGet("Codes")]
    public async Task<IActionResult> Codes(string? search)
    {
        var data = await ticketQueryService.GetCodeTrackingDataAsync(search);

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

        var result = await ticketQueryService.GetWhoHasntBoughtAsync(
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
        var aggregates = await ticketQueryService.GetSalesAggregatesAsync();

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
        await ticketSyncService.ResetSyncStateForFullResyncAsync();

        BackgroundJob.Enqueue<TicketSyncJob>(job => job.ExecuteAsync(CancellationToken.None));
        SetSuccess("Full re-sync triggered. All orders will be re-fetched.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Participation/Backfill")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> ParticipationBackfill()
    {
        var model = new ParticipationBackfillViewModel
        {
            Year = await participationBackfillService.GetDefaultYearAsync(),
        };
        return View(model);
    }

    [HttpPost("Participation/Backfill")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> ParticipationBackfill(ParticipationBackfillViewModel model)
    {
        if (!ModelState.IsValid)
        {
            SetError("Please provide CSV data with UserId and Status columns.");
            return View(model);
        }

        try
        {
            var result = await participationBackfillService.BackfillFromCsvAsync(model.Year, model.CsvData);
            if (!result.Succeeded)
            {
                SetError(result.Message);
                return View(model);
            }

            SetSuccess(result.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to backfill participation data for year {Year}", model.Year);
            SetError("Failed to process backfill data. Check the format and try again.");
            return View(model);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Export/Attendees")]
    [Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
    public async Task<IActionResult> ExportAttendees()
    {
        var rows = await ticketQueryService.GetAttendeeExportDataAsync();

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
        var rows = await ticketQueryService.GetOrderExportDataAsync();

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

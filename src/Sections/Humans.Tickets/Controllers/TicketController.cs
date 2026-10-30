using Humans.Tickets.Contracts;
using Humans.Finance.Contracts;
using CsvHelper;
using Hangfire;
using Humans.Base.Csv;
using Humans.Tickets.Jobs;
using Humans.Tickets.Services;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Tickets.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Tickets.Controllers;

[Authorize(Policy = TicketsPolicies.Read)]
[Route("Tickets")]
internal sealed class TicketController(
    ITicketManagement ticketQueryService,
    ITicketSyncService ticketSyncService,
    IUserParticipationBackfillService participationBackfillService,
    TicketDashboardPageBuilder dashboardPageBuilder,
    IUserService userService,
    ILogger<TicketController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        return View(await dashboardPageBuilder.BuildAsync(FinanceRoles.IsFinanceAdmin(User)));
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
            filterPaymentStatus, filterTicketType, filterMatched, HttpContext.RequestAborted);

        var model = new TicketOrdersViewModel
        {
            Orders = result.Rows,
            TotalCount = result.TotalCount,
            Page = page,
            PageSize = pageSize,
            Search = search,
            SortBy = sortBy,
            SortDesc = sortDesc,
            FilterPaymentStatus = filterPaymentStatus,
            FilterTicketType = filterTicketType,
            FilterMatched = filterMatched,
            AvailableTicketTypes = (await ticketQueryService.GetAvailableTicketTypesAsync(HttpContext.RequestAborted))
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
            filterTicketType, filterStatus, filterMatched, filterOrderId, filterMultipleTickets, HttpContext.RequestAborted);

        var model = new TicketAttendeesViewModel
        {
            Attendees = result.Rows,
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
            AvailableTicketTypes = (await ticketQueryService.GetAvailableTicketTypesAsync(HttpContext.RequestAborted))
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList(),
        };

        return View(model);
    }

    [HttpGet("Codes")]
    public async Task<IActionResult> Codes(string? search)
    {
        var data = await ticketQueryService.GetCodeTrackingDataAsync(search, HttpContext.RequestAborted);

        var model = new TicketCodeTrackingViewModel
        {
            TotalCodesSent = data.TotalCodesSent,
            CodesRedeemed = data.CodesRedeemed,
            CodesUnused = data.CodesUnused,
            RedemptionRate = data.RedemptionRate,
            Campaigns = data.Campaigns,
            Codes = data.Codes,
            Search = search,
        };

        return View(model);
    }

    [HttpGet("WhoHasntBought")]
    public async Task<IActionResult> WhoHasntBought(
        string? search, string? filterTeam = null, string? filterTier = null,
        string? filterTicketStatus = null,
        int page = 1, int pageSize = 25)
    {
        pageSize = pageSize.ClampPageSize();

        var result = await ticketQueryService.GetWhoHasntBoughtAsync(
            search, filterTeam, filterTier, filterTicketStatus, page, pageSize, HttpContext.RequestAborted);

        var model = new WhoHasntBoughtViewModel
        {
            Humans = result.Humans,
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
        var aggregates = await ticketQueryService.GetSalesAggregatesAsync(HttpContext.RequestAborted);

        var model = new TicketSalesAggregatesViewModel
        {
            WeeklySales = aggregates.WeeklySales,
            QuarterlySales = aggregates.QuarterlySales,
            MonthlySales = aggregates.MonthlySales,
            ByTicketType = aggregates.ByTicketType,
            ByDiscountCampaign = aggregates.ByDiscountCampaign,
        };

        return View(model);
    }

    [HttpPost("Sync")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = TicketsPolicies.Manage)]
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
            Year = await participationBackfillService.GetDefaultYearAsync(HttpContext.RequestAborted),
        };
        return View(model);
    }

    [HttpPost("Participation/Backfill")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> ParticipationBackfill(ParticipationBackfillViewModel model)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();

        if (!ModelState.IsValid)
        {
            SetError("Please provide CSV data with UserId and Status columns.");
            return View(model);
        }

        try
        {
            var result = await participationBackfillService.BackfillFromCsvAsync(model.Year, model.CsvData, actorId.Value,
                apply: model.Confirmed);
            if (!result.Succeeded)
            {
                SetError(result.Message);
                return View(model);
            }

            if (result.PreviewCsv is not null)
            {
                model.CsvData = result.PreviewCsv;
                model.IsPreview = true;
                model.PreviewCount = result.Count;
                ModelState.Remove(nameof(model.CsvData));
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
    [Authorize(Policy = TicketsPolicies.Manage)]
    public async Task<IActionResult> ExportAttendees()
    {
        var actorId = GetCurrentUserId();
        if (actorId is null)
            return Forbid();

        var rows = await ticketQueryService.GetAttendeeExportDataAsync(actorId.Value, HttpContext.RequestAborted);

        var bytes = HumansCsv.WriteBytes(csv =>
        {
            csv.WriteRow("Name", "Email", "Ticket Type", "Price", "Status", "Order ID");
            foreach (var a in rows.OrderBy(a => a.AttendeeName, StringComparer.CurrentCulture))
            {
                csv.WriteRow(a.AttendeeName, a.AttendeeEmail, a.TicketTypeName, a.Price, a.Status, a.VendorOrderId);
            }
        });
        return File(bytes, "text/csv", "attendees-export.csv");
    }

    [HttpGet("Export/Orders")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    public async Task<IActionResult> ExportOrders()
    {
        var actorId = GetCurrentUserId();
        if (actorId is null)
            return Forbid();

        var rows = await ticketQueryService.GetOrderExportDataAsync(actorId.Value, HttpContext.RequestAborted);

        var bytes = HumansCsv.WriteBytes(csv =>
        {
            csv.WriteRow("Date", "Purchaser", "Email", "Tickets", "Amount", "Currency",
                "Code", "Discount", "Donation", "VAT", "Payment Method", "Stripe Fee", "TT Fee", "Stripe Fee VAT", "Withheld Tax", "Status");
            foreach (var o in rows.OrderByDescending(o => o.PurchasedAt))
            {
                csv.WriteRow(
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
                    o.FeeTax,
                    o.WithheldTax,
                    o.PaymentStatus);
            }
        });
        return File(bytes, "text/csv", "orders-export.csv");
    }

    [HttpGet("Export/AccountantReport")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    public async Task<IActionResult> ExportAccountantReport()
    {
        var months = (await ticketQueryService.GetSalesAggregatesAsync(HttpContext.RequestAborted)).MonthlySales;

        var bytes = HumansCsv.WriteBytes(csv =>
        {
            csv.WriteRow("Month", "Orders", "Tickets", "Gross Collected", "Standalone Donations", "VIP Donations",
                "Ticket Income incl VAT", "VAT 10%", "Ticket Income ex VAT", "Stripe Fees", "Ticket Tailor Fees",
                "Stripe Fee VAT", "Withheld Tax", "Refunded Orders Gross");
            foreach (var m in months)
            {
                WriteAccountantRow(csv, m.MonthLabel, m.OrderCount, m.TicketsSold, m.GrossRevenue, m.Donations,
                    m.VipDonations, m.TicketIncomeInclVat, m.VatAmount, m.TicketIncomeExVat, m.StripeFees,
                    m.ApplicationFees, m.FeeTax, m.WithheldTax, m.RefundedGross);
            }
            WriteAccountantRow(csv, "Total", months.Sum(m => m.OrderCount), months.Sum(m => m.TicketsSold),
                months.Sum(m => m.GrossRevenue), months.Sum(m => m.Donations), months.Sum(m => m.VipDonations),
                months.Sum(m => m.TicketIncomeInclVat), months.Sum(m => m.VatAmount),
                months.Sum(m => m.TicketIncomeExVat), months.Sum(m => m.StripeFees),
                months.Sum(m => m.ApplicationFees), months.Sum(m => m.FeeTax), months.Sum(m => m.WithheldTax),
                months.Sum(m => m.RefundedGross));
        });
        return File(bytes, "text/csv", "ticket-income-by-month.csv");
    }

    [HttpGet("Export/Donations")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> ExportDonations()
    {
        var actorId = GetCurrentUserId();
        if (actorId is null)
            return Forbid();

        var rows = await ticketQueryService.GetDonationExportDataAsync(actorId.Value, HttpContext.RequestAborted);

        var bytes = HumansCsv.WriteBytes(csv =>
        {
            csv.WriteRow("Date", "Order ID", "Name", "Email", "Type", "Amount");
            foreach (var r in rows)
            {
                csv.WriteRow(r.Date, r.VendorOrderId, r.BuyerName, r.BuyerEmail, r.Type, r.Amount);
            }
            csv.WriteRow("Total", "", "", "", "", rows.Sum(r => r.Amount));
        });
        return File(bytes, "text/csv", "ticketing-donations.csv");
    }

    private static void WriteAccountantRow(CsvWriter csv, string label, int orders, int tickets, decimal gross,
        decimal donations, decimal vipDonations, decimal ticketIncomeInclVat, decimal vat,
        decimal ticketIncomeExVat, decimal stripeFees, decimal ttFees, decimal feeTax, decimal withheldTax,
        decimal refundedGross)
        => csv.WriteRow(label, orders, tickets, gross, donations, vipDonations,
            ticketIncomeInclVat, vat, ticketIncomeExVat, stripeFees, ttFees, feeTax, withheldTax, refundedGross);
}

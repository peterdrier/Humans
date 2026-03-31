using Hangfire;
using Humans.Application;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.TicketAdminBoardOrAdmin)]
[Route("Tickets")]
public class TicketController : HumansControllerBase
{
    private readonly HumansDbContext _dbContext;
    private readonly ITicketVendorService _vendorService;
    private readonly TicketVendorSettings _settings;
    private readonly ITicketQueryService _ticketQueryService;
    private readonly ILogger<TicketController> _logger;

    public TicketController(
        HumansDbContext dbContext,
        ITicketVendorService vendorService,
        IOptions<TicketVendorSettings> settings,
        ITicketQueryService ticketQueryService,
        UserManager<User> userManager,
        ILogger<TicketController> logger)
        : base(userManager)
    {
        _dbContext = dbContext;
        _vendorService = vendorService;
        _settings = settings.Value;
        _ticketQueryService = ticketQueryService;
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

        var query = BuildOrdersQuery(search, filterPaymentStatus, filterTicketType, filterMatched);

        var totalCount = await query.CountAsync();

        query = ApplyOrderSorting(query, sortBy, sortDesc);

        var orderRows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new TicketOrderRow
            {
                Id = o.Id,
                VendorOrderId = o.VendorOrderId,
                PurchasedAt = o.PurchasedAt,
                BuyerName = o.BuyerName,
                BuyerEmail = o.BuyerEmail,
                AttendeeCount = o.Attendees.Count,
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
                MatchedUserName = o.MatchedUser != null ? o.MatchedUser.DisplayName : null
            })
            .ToListAsync();

        var model = new TicketOrdersViewModel
        {
            Orders = orderRows,
            TotalCount = totalCount,
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

        var query = BuildAttendeesQuery(search, filterTicketType, filterStatus, filterMatched, filterOrderId);

        var totalCount = await query.CountAsync();

        query = ApplyAttendeeSorting(query, sortBy, sortDesc);

        var attendeeRows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new TicketAttendeeRow
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
                MatchedUserName = a.MatchedUser != null ? a.MatchedUser.DisplayName : null,
                VendorOrderId = a.TicketOrder.VendorOrderId
            })
            .ToListAsync();

        var model = new TicketAttendeesViewModel
        {
            Attendees = attendeeRows,
            TotalCount = totalCount,
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

        var matchedUserIds = await _ticketQueryService.GetAllMatchedUserIdsAsync();

        // Load ALL active humans (not just unmatched) so we can toggle between views
        var users = await _dbContext.Users
            .Include(u => u.Profile)
            .Include(u => u.UserEmails)
            .Include(u => u.TeamMemberships).ThenInclude(tm => tm.Team)
            .AsSplitQuery()
            .ToListAsync();

        var volunteersTeamId = Domain.Constants.SystemTeamIds.Volunteers;

        var activeHumans = users
            .Where(u => u.Profile is not null &&
                u.TeamMemberships.Any(tm => tm.TeamId == volunteersTeamId && tm.LeftAt == null))
            .ToList();

        var filteredHumans = FilterWhoHasntBoughtHumans(
            activeHumans,
            matchedUserIds,
            filterTicketStatus,
            filterTeam,
            filterTier,
            search);

        var totalCount = filteredHumans.Count;
        var pagedHumans = filteredHumans
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new WhoHasntBoughtRow
            {
                UserId = u.Id,
                HasTicket = matchedUserIds.Contains(u.Id),
                Name = u.DisplayName,
                Email = u.UserEmails.FirstOrDefault(e => e.IsNotificationTarget)?.Email ?? string.Empty,
                Teams = string.Join(", ", u.TeamMemberships
                    .Where(tm => tm.LeftAt == null)
                    .Select(tm => tm.Team.Name)
                    .Order(StringComparer.OrdinalIgnoreCase)),
                Tier = u.Profile?.MembershipTier ?? MembershipTier.Volunteer,
            })
            .ToList();

        var teams = activeHumans
            .SelectMany(u => u.TeamMemberships)
            .Where(tm => tm.LeftAt == null)
            .Select(tm => tm.Team.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var model = new WhoHasntBoughtViewModel
        {
            Humans = pagedHumans,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            Search = search,
            FilterTeam = filterTeam,
            FilterTier = filterTier,
            FilterTicketStatus = filterTicketStatus,
            AvailableTeams = teams,
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
        var syncState = await _dbContext.TicketSyncStates.FindAsync(1);
        if (syncState is not null)
        {
            syncState.LastSyncAt = null;
            await _dbContext.SaveChangesAsync();
        }

        BackgroundJob.Enqueue<TicketSyncJob>(job => job.ExecuteAsync(CancellationToken.None));
        SetSuccess("Full re-sync triggered. All orders will be re-fetched.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Export/Attendees")]
    [Authorize(Roles = RoleGroups.TicketAdminOrAdmin)]
    public async Task<IActionResult> ExportAttendees()
    {
        var attendeeList = await _dbContext.TicketAttendees
            .Include(a => a.TicketOrder)
            .OrderBy(a => a.AttendeeName)
            .ToListAsync();

        var csv = new System.Text.StringBuilder();
        csv.AppendCsvRow("Name", "Email", "Ticket Type", "Price", "Status", "Order ID");
        foreach (var a in attendeeList)
        {
            csv.AppendCsvRow(a.AttendeeName, a.AttendeeEmail, a.TicketTypeName, a.Price, a.Status, a.TicketOrder.VendorOrderId);
        }

        return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv", "attendees-export.csv");
    }

    [HttpGet("Export/Orders")]
    [Authorize(Roles = RoleGroups.TicketAdminOrAdmin)]
    public async Task<IActionResult> ExportOrders()
    {
        var orderList = await _dbContext.TicketOrders
            .Include(o => o.Attendees)
            .OrderByDescending(o => o.PurchasedAt)
            .ToListAsync();

        var csv = new System.Text.StringBuilder();
        csv.AppendCsvRow("Date", "Purchaser", "Email", "Tickets", "Amount", "Currency",
            "Code", "Discount", "Donation", "VAT", "Payment Method", "Stripe Fee", "TT Fee", "Status");
        foreach (var o in orderList)
        {
            var method = o.PaymentMethodDetail != null ? $"{o.PaymentMethod}/{o.PaymentMethodDetail}" : o.PaymentMethod;
            csv.AppendCsvRow(
                o.PurchasedAt.InUtc().Date.ToIsoDateString(),
                o.BuyerName,
                o.BuyerEmail,
                o.Attendees.Count,
                o.TotalAmount,
                o.Currency,
                o.DiscountCode,
                o.DiscountAmount,
                o.DonationAmount,
                o.VatAmount,
                method,
                o.StripeFee,
                o.ApplicationFee,
                o.PaymentStatus);
        }

        return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv", "orders-export.csv");
    }

    private IQueryable<TicketOrder> BuildOrdersQuery(
        string? search,
        string? filterPaymentStatus,
        string? filterTicketType,
        bool? filterMatched)
    {
        var query = _dbContext.TicketOrders
            .Include(o => o.Attendees)
            .Include(o => o.MatchedUser)
            .AsQueryable();

        if (search.HasSearchTerm(1))
        {
            query = query.WhereAnyContainsInsensitive(
                search,
                o => o.BuyerName,
                o => o.BuyerEmail,
                o => o.VendorOrderId,
                o => o.DiscountCode);
        }

        if (!string.IsNullOrEmpty(filterPaymentStatus) &&
            Enum.TryParse<TicketPaymentStatus>(filterPaymentStatus, true, out var paymentStatus))
        {
            query = query.Where(o => o.PaymentStatus == paymentStatus);
        }

        if (!string.IsNullOrEmpty(filterTicketType))
        {
            query = query.Where(o => o.Attendees.Any(a => a.TicketTypeName == filterTicketType));
        }

        if (filterMatched == true)
        {
            query = query.Where(o => o.MatchedUserId != null);
        }
        else if (filterMatched == false)
        {
            query = query.Where(o => o.MatchedUserId == null);
        }

        return query;
    }

    private static IQueryable<TicketOrder> ApplyOrderSorting(
        IQueryable<TicketOrder> query,
        string? sortBy,
        bool sortDesc)
    {
        if (string.Equals(sortBy, "amount", StringComparison.OrdinalIgnoreCase))
        {
            return sortDesc ? query.OrderByDescending(o => o.TotalAmount) : query.OrderBy(o => o.TotalAmount);
        }

        if (string.Equals(sortBy, "name", StringComparison.OrdinalIgnoreCase))
        {
            return sortDesc ? query.OrderByDescending(o => o.BuyerName) : query.OrderBy(o => o.BuyerName);
        }

        if (string.Equals(sortBy, "tickets", StringComparison.OrdinalIgnoreCase))
        {
            return sortDesc ? query.OrderByDescending(o => o.Attendees.Count) : query.OrderBy(o => o.Attendees.Count);
        }

        return sortDesc ? query.OrderByDescending(o => o.PurchasedAt) : query.OrderBy(o => o.PurchasedAt);
    }

    private IQueryable<TicketAttendee> BuildAttendeesQuery(
        string? search,
        string? filterTicketType,
        string? filterStatus,
        bool? filterMatched,
        string? filterOrderId)
    {
        var query = _dbContext.TicketAttendees
            .Include(a => a.MatchedUser)
            .Include(a => a.TicketOrder)
            .AsQueryable();

        if (!string.IsNullOrEmpty(filterOrderId))
        {
            query = query.Where(a => a.TicketOrder.VendorOrderId == filterOrderId);
        }

        if (search.HasSearchTerm(1))
        {
            query = query.WhereAnyContainsInsensitive(
                search,
                a => a.AttendeeName,
                a => a.AttendeeEmail);
        }

        if (!string.IsNullOrEmpty(filterTicketType))
        {
            query = query.Where(a => a.TicketTypeName == filterTicketType);
        }

        if (!string.IsNullOrEmpty(filterStatus) &&
            Enum.TryParse<TicketAttendeeStatus>(filterStatus, true, out var status))
        {
            query = query.Where(a => a.Status == status);
        }

        if (filterMatched == true)
        {
            query = query.Where(a => a.MatchedUserId != null);
        }
        else if (filterMatched == false)
        {
            query = query.Where(a => a.MatchedUserId == null);
        }

        return query;
    }

    private static IQueryable<TicketAttendee> ApplyAttendeeSorting(
        IQueryable<TicketAttendee> query,
        string? sortBy,
        bool sortDesc)
    {
        if (string.Equals(sortBy, "type", StringComparison.OrdinalIgnoreCase))
        {
            return sortDesc ? query.OrderByDescending(a => a.TicketTypeName) : query.OrderBy(a => a.TicketTypeName);
        }

        if (string.Equals(sortBy, "price", StringComparison.OrdinalIgnoreCase))
        {
            return sortDesc ? query.OrderByDescending(a => a.Price) : query.OrderBy(a => a.Price);
        }

        if (string.Equals(sortBy, "status", StringComparison.OrdinalIgnoreCase))
        {
            return sortDesc ? query.OrderByDescending(a => a.Status) : query.OrderBy(a => a.Status);
        }

        return sortDesc ? query.OrderByDescending(a => a.AttendeeName) : query.OrderBy(a => a.AttendeeName);
    }

    private static List<User> FilterWhoHasntBoughtHumans(
        IEnumerable<User> humans,
        HashSet<Guid> matchedUserIds,
        string? filterTicketStatus,
        string? filterTeam,
        string? filterTier,
        string? search)
    {
        var filteredHumans = humans;

        if (string.Equals(filterTicketStatus, "bought", StringComparison.OrdinalIgnoreCase))
        {
            filteredHumans = filteredHumans.Where(u => matchedUserIds.Contains(u.Id));
        }
        else if (string.Equals(filterTicketStatus, "not_bought", StringComparison.OrdinalIgnoreCase))
        {
            filteredHumans = filteredHumans.Where(u => !matchedUserIds.Contains(u.Id));
        }

        if (!string.IsNullOrEmpty(filterTeam))
        {
            filteredHumans = filteredHumans.Where(u =>
                u.TeamMemberships.Any(tm =>
                    tm.LeftAt == null &&
                    string.Equals(tm.Team.Name, filterTeam, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrEmpty(filterTier) && Enum.TryParse<MembershipTier>(filterTier, ignoreCase: true, out var parsedTier))
        {
            filteredHumans = filteredHumans.Where(u => u.Profile?.MembershipTier == parsedTier);
        }

        if (search.HasSearchTerm(1))
        {
            filteredHumans = filteredHumans.Where(u =>
                u.DisplayName.ContainsOrdinalIgnoreCase(search) ||
                u.UserEmails.Any(e => e.Email.ContainsOrdinalIgnoreCase(search)));
        }

        return filteredHumans.ToList();
    }
}

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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.TicketAdminBoardOrAdmin)]
[Route("Tickets")]
public class TicketController : HumansControllerBase
{
    private static readonly TimeSpan EventSummaryCacheTtl = TimeSpan.FromMinutes(15);

    private readonly HumansDbContext _dbContext;
    private readonly ITicketVendorService _vendorService;
    private readonly TicketVendorSettings _settings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TicketController> _logger;

    public TicketController(
        HumansDbContext dbContext,
        ITicketVendorService vendorService,
        IOptions<TicketVendorSettings> settings,
        IMemoryCache cache,
        UserManager<User> userManager,
        ILogger<TicketController> logger)
        : base(userManager)
    {
        _dbContext = dbContext;
        _vendorService = vendorService;
        _settings = settings.Value;
        _cache = cache;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var isConfigured = _settings.IsConfigured;
        var syncState = await _dbContext.TicketSyncStates.FindAsync(1);

        // Reset stuck Running state — if Running for > 30 min, treat as stale (crash recovery)
        if (syncState is { SyncStatus: TicketSyncStatus.Running, StatusChangedAt: not null })
        {
            var elapsed = SystemClock.Instance.GetCurrentInstant() - syncState.StatusChangedAt.Value;
            if (elapsed > Duration.FromMinutes(30))
            {
                syncState.SyncStatus = TicketSyncStatus.Error;
                syncState.LastError = "Sync state was stuck in Running for >30 minutes (likely crash). Auto-reset.";
                syncState.StatusChangedAt = SystemClock.Instance.GetCurrentInstant();
                await _dbContext.SaveChangesAsync();
            }
        }

        if (!isConfigured)
        {
            return View(new TicketDashboardViewModel { IsConfigured = false });
        }

        // Summary stats from local data
        var orders = _dbContext.TicketOrders.AsQueryable();
        var attendees = _dbContext.TicketAttendees.AsQueryable();

        // Count both Valid and CheckedIn — both are sold tickets
        var ticketsSold = await attendees.CountAsync(a =>
            a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn);
        var revenue = await orders
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid)
            .SumAsync(o => o.TotalAmount);
        var avgPrice = ticketsSold > 0 ? revenue / ticketsSold : 0;
        var unmatchedCount = await orders.CountAsync(o => o.MatchedUserId == null);

        // Cache vendor event summary (15-min TTL) to avoid API call on every page load
        int totalCapacity = 0;
        try
        {
            var summary = await _cache.GetOrCreateAsync(CacheKeys.TicketEventSummary, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = EventSummaryCacheTtl;
                return await _vendorService.GetEventSummaryAsync(_settings.EventId);
            });
            totalCapacity = summary?.TotalCapacity ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch event summary from vendor");
        }

        // Daily sales data for chart.
        // NodaTime GroupBy is not translatable by EF Core, so load in memory first.
        // At ~1,500 orders this is fine for a small nonprofit system.
        var orderDates = await orders
            .Select(o => new { o.PurchasedAt, AttendeeCount = o.Attendees.Count })
            .ToListAsync();

        var salesByDate = orderDates
            .GroupBy(o => o.PurchasedAt.InUtc().Date)
            .ToDictionary(g => g.Key, g => g.Sum(o => o.AttendeeCount));

        // Fill in zero-sale days so chart and rolling average are correct
        var dailySalesPoints = new List<DailySalesPoint>();
        if (salesByDate.Count > 0)
        {
            var startDate = salesByDate.Keys.Min();
            var endDate = salesByDate.Keys.Max();
            var allDays = new List<(LocalDate Date, int Count)>();

            for (var d = startDate; d <= endDate; d = d.PlusDays(1))
            {
                allDays.Add((d, salesByDate.GetValueOrDefault(d, 0)));
            }

            for (var i = 0; i < allDays.Count; i++)
            {
                var (date, count) = allDays[i];
                var windowStart = Math.Max(0, i - 6);
                var window = allDays.Skip(windowStart).Take(i - windowStart + 1);
                var rollingAvg = window.Average(d => (decimal)d.Count);

                dailySalesPoints.Add(new DailySalesPoint
                {
                    Date = date.ToIsoDateString(),
                    TicketsSold = count,
                    RollingAverage = Math.Round(rollingAvg, 1)
                });
            }
        }

        // Recent 10 orders
        var recentOrders = await orders
            .OrderByDescending(o => o.PurchasedAt)
            .Take(10)
            .Select(o => new TicketOrderSummary
            {
                Id = o.Id,
                BuyerName = o.BuyerName,
                TicketCount = o.Attendees.Count,
                Amount = o.TotalAmount,
                Currency = o.Currency,
                PurchasedAt = o.PurchasedAt,
                IsMatched = o.MatchedUserId != null
            })
            .ToListAsync();

        var model = new TicketDashboardViewModel
        {
            TicketsSold = ticketsSold,
            TotalCapacity = totalCapacity,
            BreakEvenTarget = _settings.BreakEvenTarget,
            Revenue = revenue,
            AveragePrice = avgPrice,
            TicketsRemaining = totalCapacity - ticketsSold,
            DailySales = dailySalesPoints,
            UnmatchedOrderCount = unmatchedCount,
            SyncStatus = syncState?.SyncStatus ?? TicketSyncStatus.Idle,
            SyncError = syncState?.LastError,
            LastSyncAt = syncState?.LastSyncAt,
            RecentOrders = recentOrders,
            IsConfigured = true,
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
            AvailableTicketTypes = await GetAvailableTicketTypesAsync(),
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
            AvailableTicketTypes = await GetAvailableTicketTypesAsync(),
        };

        return View(model);
    }

    [HttpGet("Codes")]
    public async Task<IActionResult> Codes(string? search)
    {
        var campaigns = await _dbContext.Set<Domain.Entities.Campaign>()
            .Where(c => c.Status == CampaignStatus.Active || c.Status == CampaignStatus.Completed)
            .Include(c => c.Grants).ThenInclude(g => g.Code)
            .Include(c => c.Grants).ThenInclude(g => g.User)
            .OrderByDescending(c => c.CreatedAt)
            .AsSplitQuery()
            .ToListAsync();

        var campaignSummaries = campaigns.Select(c =>
        {
            var total = c.Grants.Count;
            var redeemed = c.Grants.Count(g => g.RedeemedAt is not null);
            return new CampaignCodeSummary
            {
                CampaignId = c.Id,
                CampaignTitle = c.Title,
                TotalGrants = total,
                Redeemed = redeemed,
                Unused = total - redeemed,
                RedemptionRate = total > 0 ? Math.Round(redeemed * 100m / total, 1) : 0
            };
        }).ToList();

        var allGrants = campaigns.SelectMany(c => c.Grants).AsEnumerable();
        if (search.HasSearchTerm(1))
        {
            allGrants = allGrants.Where(g =>
                g.Code?.Code.ContainsOrdinalIgnoreCase(search) == true ||
                g.User.DisplayName.ContainsOrdinalIgnoreCase(search));
        }

        // Load orders with discount codes to show who redeemed and on which order
        var ordersWithCodes = await _dbContext.TicketOrders
            .Where(o => o.DiscountCode != null)
            .Select(o => new { o.DiscountCode, o.BuyerName, o.BuyerEmail, o.VendorOrderId })
            .ToListAsync();

        var orderByCode = ordersWithCodes
            .GroupBy(o => o.DiscountCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var codeRows = allGrants.Select(g =>
        {
            var code = g.Code?.Code;
            orderByCode.TryGetValue(code ?? "", out var matchedOrder);
            return new CodeDetailRow
            {
                Code = code ?? "\u2014",
                RecipientName = g.User.DisplayName,
                RecipientUserId = g.UserId,
                CampaignTitle = campaigns.First(c => c.Id == g.CampaignId).Title,
                Status = g.RedeemedAt is not null ? "Redeemed" : (g.LatestEmailStatus?.ToString() ?? "Pending"),
                RedeemedAt = g.RedeemedAt,
                RedeemedByName = matchedOrder?.BuyerName,
                RedeemedByEmail = matchedOrder?.BuyerEmail,
                RedeemedOrderVendorId = matchedOrder?.VendorOrderId,
            };
        }).ToList();

        var totalSent = campaignSummaries.Sum(c => c.TotalGrants);
        var totalRedeemed = campaignSummaries.Sum(c => c.Redeemed);

        var model = new TicketCodeTrackingViewModel
        {
            TotalCodesSent = totalSent,
            CodesRedeemed = totalRedeemed,
            CodesUnused = totalSent - totalRedeemed,
            RedemptionRate = totalSent > 0 ? Math.Round(totalRedeemed * 100m / totalSent, 1) : 0,
            Campaigns = campaignSummaries,
            Codes = codeRows,
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

        var matchedUserIds = await GetMatchedTicketUserIdsAsync();

        // Load ALL active humans (not just unmatched) so we can toggle between views
        var users = await _dbContext.Users
            .Include(u => u.Profile)
            .Include(u => u.UserEmails)
            .Include(u => u.TeamMemberships).ThenInclude(tm => tm.Team)
            .AsSplitQuery()
            .ToListAsync();

        var volunteersTeamId = await _dbContext.Set<Domain.Entities.Team>()
            .Where(t => t.SystemTeamType == Domain.Enums.SystemTeamType.Volunteers)
            .Select(t => t.Id)
            .OrderBy(id => id)
            .FirstOrDefaultAsync();

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
        csv.AppendCsvRow("Date", "Purchaser", "Email", "Tickets", "Amount", "Currency", "Code", "Status");
        foreach (var o in orderList)
        {
            csv.AppendCsvRow(
                o.PurchasedAt.InUtc().Date.ToIsoDateString(),
                o.BuyerName,
                o.BuyerEmail,
                o.Attendees.Count,
                o.TotalAmount,
                o.Currency,
                o.DiscountCode,
                o.PaymentStatus);
        }

        return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv", "orders-export.csv");
    }

    private Task<List<string>> GetAvailableTicketTypesAsync()
    {
        return _dbContext.TicketAttendees
            .Select(a => a.TicketTypeName)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync();
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

    private async Task<HashSet<Guid>> GetMatchedTicketUserIdsAsync()
    {
        var matchedFromAttendees = await _dbContext.TicketAttendees
            .Where(a => a.MatchedUserId != null)
            .Select(a => a.MatchedUserId!.Value)
            .Distinct()
            .ToListAsync();

        var matchedFromOrders = await _dbContext.TicketOrders
            .Where(o => o.MatchedUserId != null)
            .Select(o => o.MatchedUserId!.Value)
            .Distinct()
            .ToListAsync();

        return matchedFromAttendees.Union(matchedFromOrders).ToHashSet();
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

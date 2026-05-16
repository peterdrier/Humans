using Humans.Application.Configuration;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Microsoft.Extensions.Options;

namespace Humans.Web.Models.Tickets;

public sealed class TicketDashboardPageBuilder
{
    private readonly ITicketVendorService _vendorService;
    private readonly TicketVendorSettings _settings;
    private readonly ITicketQueryService _ticketQueryService;
    private readonly IUserService _userService;
    private readonly IShiftManagementService _shiftManagement;
    private readonly IShiftView _shiftView;
    private readonly ILogger<TicketDashboardPageBuilder> _logger;

    public TicketDashboardPageBuilder(
        ITicketVendorService vendorService,
        IOptions<TicketVendorSettings> settings,
        ITicketQueryService ticketQueryService,
        IUserService userService,
        IShiftManagementService shiftManagement,
        IShiftView shiftView,
        ILogger<TicketDashboardPageBuilder> logger)
    {
        _vendorService = vendorService;
        _settings = settings.Value;
        _ticketQueryService = ticketQueryService;
        _userService = userService;
        _shiftManagement = shiftManagement;
        _shiftView = shiftView;
        _logger = logger;
    }

    public async Task<TicketDashboardViewModel> BuildAsync(bool canAccessFinance)
    {
        if (!_settings.IsConfigured)
            return new TicketDashboardViewModel { IsConfigured = false };

        var stats = await _ticketQueryService.GetDashboardStatsAsync();
        var currency = stats.RecentOrders.FirstOrDefault()?.Currency ?? "EUR";
        var breakEven = await _ticketQueryService.CalculateBreakEvenAsync(
            stats.TicketsSold,
            stats.Revenue,
            currency,
            canAccessFinance,
            _settings.BreakEvenTarget);
        var totalCapacity = await LoadTotalCapacityAsync();

        return new TicketDashboardViewModel
        {
            TicketsSold = stats.TicketsSold,
            TotalCapacity = totalCapacity,
            BreakEvenDetail = breakEven.Detail,
            BreakEvenTarget = breakEven.Target,
            Currency = currency,
            Revenue = stats.Revenue,
            AveragePrice = stats.GrossAveragePrice,
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
                PaymentStatus = o.PaymentStatus,
            }).ToList(),
            IsConfigured = true,
            SetMembership = await BuildSetMembershipAsync()
        };
    }

    private async Task<int> LoadTotalCapacityAsync()
    {
        try
        {
            var summary = await _vendorService.GetEventSummaryAsync(_settings.EventId);
            return summary?.TotalCapacity ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch event summary from vendor");
            return 0;
        }
    }

    private async Task<UserSetMembership> BuildSetMembershipAsync()
    {
        var snapshot = await _userService.GetAllUserInfosAsync().ConfigureAwait(false);
        var activeEvent = await _shiftManagement.GetActiveAsync();
        var activeYear = activeEvent?.Year ?? 0;
        var shiftViews = await _shiftView.GetUsersAsync(snapshot.Select(u => u.Id));

        var none = 0;
        var profileOnly = 0;
        var ticketOnly = 0;
        var shiftOnly = 0;
        var profileTicket = 0;
        var profileShift = 0;
        var ticketShift = 0;
        var all = 0;

        foreach (var u in snapshot)
        {
            var p = u.IsActive;
            var t = activeYear > 0 && u.HasTicketForYear(activeYear);
            var s = shiftViews[u.Id].HasShift;

            switch ((p, t, s))
            {
                case (false, false, false): none++; break;
                case (true, false, false): profileOnly++; break;
                case (false, true, false): ticketOnly++; break;
                case (false, false, true): shiftOnly++; break;
                case (true, true, false): profileTicket++; break;
                case (true, false, true): profileShift++; break;
                case (false, true, true): ticketShift++; break;
                case (true, true, true): all++; break;
            }
        }

        return new UserSetMembership(
            None: none,
            ProfileOnly: profileOnly,
            TicketOnly: ticketOnly,
            ShiftOnly: shiftOnly,
            ProfileTicket: profileTicket,
            ProfileShift: profileShift,
            TicketShift: ticketShift,
            All: all);
    }
}

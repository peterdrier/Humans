using Humans.Application.Configuration;
using Humans.Application.Interfaces.Tickets;
using Microsoft.Extensions.Options;

namespace Humans.Web.Models.Tickets;

public sealed class TicketDashboardPageBuilder
{
    private readonly ITicketVendorService _vendorService;
    private readonly TicketVendorSettings _settings;
    private readonly ITicketQueryService _ticketQueryService;
    private readonly ILogger<TicketDashboardPageBuilder> _logger;

    public TicketDashboardPageBuilder(
        ITicketVendorService vendorService,
        IOptions<TicketVendorSettings> settings,
        ITicketQueryService ticketQueryService,
        ILogger<TicketDashboardPageBuilder> logger)
    {
        _vendorService = vendorService;
        _settings = settings.Value;
        _ticketQueryService = ticketQueryService;
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
}

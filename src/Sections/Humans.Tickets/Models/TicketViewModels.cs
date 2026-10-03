using Humans.Users.Contracts;
using NodaTime;

using Humans.Base.Models;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain;
using Humans.Tickets.Services.Dtos;

namespace Humans.Tickets.Models;

internal sealed class TicketDashboardViewModel
{
    public int TicketsSold { get; set; }
    public int TotalCapacity { get; set; }
    public decimal Revenue { get; set; }
    public decimal AveragePrice { get; set; }
    public string? BreakEvenDetail { get; set; }
    public int TicketsRemaining { get; set; }
    public int BreakEvenTarget { get; set; }
    public string Currency { get; set; } = "EUR";

    public decimal TotalStripeFees { get; set; }
    public decimal TotalApplicationFees { get; set; }
    public decimal NetRevenue { get; set; }

    public List<PaymentMethodFeeBreakdown> FeesByPaymentMethod { get; set; } = [];

    public List<DailySalesPoint> DailySales { get; set; } = [];

    public int UnmatchedOrderCount { get; set; }
    public TicketSyncStatus SyncStatus { get; set; }
    public string? SyncError { get; set; }
    public Instant? LastSyncAt { get; set; }

    public List<TicketOrderSummary> RecentOrders { get; set; } = [];

    public bool IsConfigured { get; set; }

    public int WhoHasntBoughtCount { get; set; }
}

internal sealed class PaymentMethodFeeBreakdown
{
    public string PaymentMethod { get; set; } = string.Empty;
    public int OrderCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalStripeFees { get; set; }
    public decimal TotalApplicationFees { get; set; }
    public decimal EffectiveRate { get; set; } // StripeFee as % of amount
}

internal sealed class DailySalesPoint
{
    public string Date { get; set; } = string.Empty; // "2026-05-15" for Chart.js
    public int TicketsSold { get; set; }
    public decimal? RollingAverage { get; set; } // 7-day rolling avg
}

internal sealed class TicketOrderSummary
{
    public Guid Id { get; set; }
    public string BuyerName { get; set; } = string.Empty;
    public int TicketCount { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public Instant PurchasedAt { get; set; }
    public bool IsMatched { get; set; }
    public TicketPaymentStatus PaymentStatus { get; set; }
}

internal sealed class TicketOrdersViewModel() : PagedListViewModel(25)
{
    public List<OrderRow> Orders { get; set; } = [];
    public string? Search { get; set; }
    public string SortBy { get; set; } = "date";
    public bool SortDesc { get; set; } = true;
    public string? FilterPaymentStatus { get; set; }
    public string? FilterTicketType { get; set; }
    public bool? FilterMatched { get; set; }
    public List<string> AvailableTicketTypes { get; set; } = [];
}

internal sealed class TicketAttendeesViewModel() : PagedListViewModel(25)
{
    public List<AttendeeRow> Attendees { get; set; } = [];
    public string? Search { get; set; }
    public string SortBy { get; set; } = "name";
    public bool SortDesc { get; set; }
    public string? FilterTicketType { get; set; }
    public string? FilterStatus { get; set; }
    public bool? FilterMatched { get; set; }
    public string? FilterOrderId { get; set; }
    public bool FilterMultipleTickets { get; set; }
    public List<string> AvailableTicketTypes { get; set; } = [];
}

internal sealed class TicketCodeTrackingViewModel
{
    public int TotalCodesSent { get; set; }
    public int CodesRedeemed { get; set; }
    public int CodesUnused { get; set; }
    public decimal RedemptionRate { get; set; }
    public List<CampaignCodeSummaryDto> Campaigns { get; set; } = [];
    public List<CodeDetailDto> Codes { get; set; } = [];
    public string? Search { get; set; }
}

internal sealed class TicketSalesAggregatesViewModel
{
    public List<WeeklySalesAggregate> WeeklySales { get; set; } = [];
    public List<QuarterlySalesAggregate> QuarterlySales { get; set; } = [];
    public List<MonthlySalesAggregate> MonthlySales { get; set; } = [];
    public List<TicketTypeSalesAggregate> ByTicketType { get; set; } = [];
    public List<DiscountCampaignAggregate> ByDiscountCampaign { get; set; } = [];
    public string Currency { get; set; } = "EUR";
}

internal sealed class WhoHasntBoughtViewModel() : PagedListViewModel(25)
{
    public List<WhoHasntBoughtRowDto> Humans { get; set; } = [];
    public string? Search { get; set; }
    public string? FilterTeam { get; set; }
    public string? FilterTier { get; set; }
    public string? FilterTicketStatus { get; set; } // "bought", "not_bought", or null (all)
    public List<string> AvailableTeams { get; set; } = [];
}

internal sealed class ParticipationBackfillViewModel
{
    public int Year { get; set; }
    public string? CsvData { get; set; }
}

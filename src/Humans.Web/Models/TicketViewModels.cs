using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models;

public class TicketDashboardViewModel
{
    public int TicketsSold { get; set; }
    public int TotalCapacity { get; set; }
    public decimal Revenue { get; set; }
    public decimal AveragePrice { get; set; }
    public int TicketsRemaining { get; set; }
    public int BreakEvenTarget { get; set; }
    public string Currency { get; set; } = "EUR";

    // Fee tracking
    public decimal TotalStripeFees { get; set; }
    public decimal TotalApplicationFees { get; set; }
    public decimal NetRevenue { get; set; }

    // Fee breakdown by payment method
    public List<PaymentMethodFeeBreakdown> FeesByPaymentMethod { get; set; } = [];

    // Daily sales chart data
    public List<DailySalesPoint> DailySales { get; set; } = [];

    // Problems / attention items
    public int UnmatchedOrderCount { get; set; }
    public TicketSyncStatus SyncStatus { get; set; }
    public string? SyncError { get; set; }
    public Instant? LastSyncAt { get; set; }

    // Recent orders (last 10)
    public List<TicketOrderSummary> RecentOrders { get; set; } = [];

    public bool IsConfigured { get; set; }

    // Who hasn't bought count (for dashboard link)
    public int WhoHasntBoughtCount { get; set; }

    // Volunteer ticket coverage
    public int TotalActiveVolunteers { get; set; }
    public int VolunteersWithTickets { get; set; }
    public decimal VolunteerCoveragePercent { get; set; }
}

public class PaymentMethodFeeBreakdown
{
    public string PaymentMethod { get; set; } = string.Empty;
    public int OrderCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalStripeFees { get; set; }
    public decimal TotalApplicationFees { get; set; }
    public decimal EffectiveRate { get; set; } // StripeFee as % of amount
}

public class DailySalesPoint
{
    public string Date { get; set; } = string.Empty; // "2026-05-15" for Chart.js
    public int TicketsSold { get; set; }
    public decimal? RollingAverage { get; set; } // 7-day rolling avg
}

public class TicketOrderSummary
{
    public Guid Id { get; set; }
    public string BuyerName { get; set; } = string.Empty;
    public int TicketCount { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public Instant PurchasedAt { get; set; }
    public bool IsMatched { get; set; }
}

public class TicketOrdersViewModel : PagedListViewModel
{
    public TicketOrdersViewModel() : base(25)
    {
    }

    public List<TicketOrderRow> Orders { get; set; } = [];
    public string? Search { get; set; }
    public string SortBy { get; set; } = "date";
    public bool SortDesc { get; set; } = true;
    public string? FilterPaymentStatus { get; set; }
    public string? FilterTicketType { get; set; }
    public bool? FilterMatched { get; set; }
    public List<string> AvailableTicketTypes { get; set; } = [];
}

public class TicketOrderRow
{
    public Guid Id { get; set; }
    public Instant PurchasedAt { get; set; }
    public string VendorOrderId { get; set; } = string.Empty;
    public string BuyerName { get; set; } = string.Empty;
    public string BuyerEmail { get; set; } = string.Empty;
    public int AttendeeCount { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? DiscountCode { get; set; }
    public decimal? DiscountAmount { get; set; }
    public decimal DonationAmount { get; set; }
    public decimal VatAmount { get; set; }
    public string? PaymentMethod { get; set; }
    public string? PaymentMethodDetail { get; set; }
    public decimal? StripeFee { get; set; }
    public decimal? ApplicationFee { get; set; }
    public TicketPaymentStatus PaymentStatus { get; set; }
    public string? VendorDashboardUrl { get; set; }
    public Guid? MatchedUserId { get; set; }
    public string? MatchedUserName { get; set; }
}

public class TicketAttendeesViewModel : PagedListViewModel
{
    public TicketAttendeesViewModel() : base(25)
    {
    }

    public List<TicketAttendeeRow> Attendees { get; set; } = [];
    public string? Search { get; set; }
    public string SortBy { get; set; } = "name";
    public bool SortDesc { get; set; }
    public string? FilterTicketType { get; set; }
    public string? FilterStatus { get; set; }
    public bool? FilterMatched { get; set; }
    public string? FilterOrderId { get; set; }
    public List<string> AvailableTicketTypes { get; set; } = [];
}

public class TicketAttendeeRow
{
    public Guid Id { get; set; }
    public string AttendeeName { get; set; } = string.Empty;
    public string? AttendeeEmail { get; set; }
    public string TicketTypeName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public bool IsVip { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal VipDonation { get; set; }
    public TicketAttendeeStatus Status { get; set; }
    public Guid? MatchedUserId { get; set; }
    public string? MatchedUserName { get; set; }
    public string VendorOrderId { get; set; } = string.Empty;
}

public class TicketCodeTrackingViewModel
{
    public int TotalCodesSent { get; set; }
    public int CodesRedeemed { get; set; }
    public int CodesUnused { get; set; }
    public decimal RedemptionRate { get; set; }
    public List<CampaignCodeSummary> Campaigns { get; set; } = [];
    public List<CodeDetailRow> Codes { get; set; } = [];
    public string? Search { get; set; }
}

public class CodeDetailRow
{
    public string Code { get; set; } = string.Empty;
    public string RecipientName { get; set; } = string.Empty;
    public Guid RecipientUserId { get; set; }
    public string CampaignTitle { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Instant? RedeemedAt { get; set; }
    public string? RedeemedByName { get; set; }
    public string? RedeemedByEmail { get; set; }
    public string? RedeemedOrderVendorId { get; set; }
}

public class CampaignCodeSummary
{
    public Guid CampaignId { get; set; }
    public string CampaignTitle { get; set; } = string.Empty;
    public int TotalGrants { get; set; }
    public int Redeemed { get; set; }
    public int Unused { get; set; }
    public decimal RedemptionRate { get; set; }
}

public class TicketSalesAggregatesViewModel
{
    public List<WeeklySalesRow> WeeklySales { get; set; } = [];
    public List<QuarterlySalesRow> QuarterlySales { get; set; } = [];
    public string Currency { get; set; } = "EUR";
}

public class WeeklySalesRow
{
    public string WeekLabel { get; set; } = string.Empty; // "Mar 3 – Mar 9"
    public int TicketsSold { get; set; }
    public decimal GrossRevenue { get; set; }
    public int OrderCount { get; set; }
    public decimal Donations { get; set; }
    public decimal VatAmount { get; set; }
    public decimal VipDonations { get; set; }
}

public class QuarterlySalesRow
{
    public string QuarterLabel { get; set; } = string.Empty; // "Q1 2026"
    public int Year { get; set; }
    public int Quarter { get; set; }
    public int TicketsSold { get; set; }
    public decimal GrossRevenue { get; set; }
    public int OrderCount { get; set; }
    public decimal Donations { get; set; }
    public decimal VatAmount { get; set; }
    public decimal VipDonations { get; set; }
}

public class WhoHasntBoughtViewModel : PagedListViewModel
{
    public WhoHasntBoughtViewModel() : base(25)
    {
    }

    public List<WhoHasntBoughtRow> Humans { get; set; } = [];
    public string? Search { get; set; }
    public string? FilterTeam { get; set; }
    public string? FilterTier { get; set; }
    public string? FilterTicketStatus { get; set; } // "bought", "not_bought", or null (all)
    public List<string> AvailableTeams { get; set; } = [];
}

public class WhoHasntBoughtRow
{
    public Guid UserId { get; set; }
    public bool HasTicket { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Teams { get; set; } = string.Empty;
    public MembershipTier Tier { get; set; }
}

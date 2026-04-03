using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.DTOs;

/// <summary>Aggregated ticket dashboard statistics.</summary>
public class TicketDashboardStats
{
    public int TicketsSold { get; init; }
    public decimal Revenue { get; init; }
    public decimal TotalStripeFees { get; init; }
    public decimal TotalApplicationFees { get; init; }
    public decimal NetRevenue { get; init; }
    public decimal AveragePrice { get; init; }
    public int UnmatchedOrderCount { get; init; }

    public List<FeeBreakdownByMethod> FeesByPaymentMethod { get; init; } = [];
    public List<DailySales> DailySalesPoints { get; init; } = [];
    public List<RecentOrder> RecentOrders { get; init; } = [];

    // Sync state
    public TicketSyncStatus SyncStatus { get; init; }
    public string? SyncError { get; init; }
    public Instant? LastSyncAt { get; init; }

    // Volunteer ticket coverage
    public int TotalActiveVolunteers { get; init; }
    public int VolunteersWithTickets { get; init; }
    public decimal VolunteerCoveragePercent { get; init; }
}

public class FeeBreakdownByMethod
{
    public string PaymentMethod { get; init; } = string.Empty;
    public int OrderCount { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal TotalStripeFees { get; init; }
    public decimal TotalApplicationFees { get; init; }
    public decimal EffectiveRate { get; init; }
}

public class DailySales
{
    public string Date { get; init; } = string.Empty;
    public int TicketsSold { get; init; }
    public decimal? RollingAverage { get; init; }
}

public class RecentOrder
{
    public Guid Id { get; init; }
    public string BuyerName { get; init; } = string.Empty;
    public int TicketCount { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "EUR";
    public Instant PurchasedAt { get; init; }
    public bool IsMatched { get; init; }
}

/// <summary>Weekly and quarterly sales aggregates for reporting.</summary>
public class TicketSalesAggregates
{
    public List<WeeklySalesAggregate> WeeklySales { get; init; } = [];
    public List<QuarterlySalesAggregate> QuarterlySales { get; init; } = [];
}

public class WeeklySalesAggregate
{
    public string WeekLabel { get; init; } = string.Empty;
    public int TicketsSold { get; init; }
    public decimal GrossRevenue { get; init; }
    public int OrderCount { get; init; }
    public decimal Donations { get; init; }
    public decimal VatAmount { get; init; }
    public decimal VipDonations { get; init; }
}

public class QuarterlySalesAggregate
{
    public string QuarterLabel { get; init; } = string.Empty;
    public int Year { get; init; }
    public int Quarter { get; init; }
    public int TicketsSold { get; init; }
    public decimal GrossRevenue { get; init; }
    public int OrderCount { get; init; }
    public decimal Donations { get; init; }
    public decimal VatAmount { get; init; }
    public decimal VipDonations { get; init; }
}

/// <summary>Aggregated code tracking data: campaign summaries + individual code details.</summary>
public class CodeTrackingData
{
    public int TotalCodesSent { get; init; }
    public int CodesRedeemed { get; init; }
    public int CodesUnused { get; init; }
    public decimal RedemptionRate { get; init; }
    public List<CampaignCodeSummaryDto> Campaigns { get; init; } = [];
    public List<CodeDetailDto> Codes { get; init; } = [];
}

public class CampaignCodeSummaryDto
{
    public Guid CampaignId { get; init; }
    public string CampaignTitle { get; init; } = string.Empty;
    public int TotalGrants { get; init; }
    public int Redeemed { get; init; }
    public int Unused { get; init; }
    public decimal RedemptionRate { get; init; }
}

public class CodeDetailDto
{
    public string Code { get; init; } = string.Empty;
    public string RecipientName { get; init; } = string.Empty;
    public Guid RecipientUserId { get; init; }
    public string CampaignTitle { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public Instant? RedeemedAt { get; init; }
    public string? RedeemedByName { get; init; }
    public string? RedeemedByEmail { get; init; }
    public string? RedeemedOrderVendorId { get; init; }
}

/// <summary>A single row in the orders list page.</summary>
public class OrderRow
{
    public Guid Id { get; init; }
    public Instant PurchasedAt { get; init; }
    public string VendorOrderId { get; init; } = string.Empty;
    public string BuyerName { get; init; } = string.Empty;
    public string BuyerEmail { get; init; } = string.Empty;
    public int AttendeeCount { get; init; }
    public decimal TotalAmount { get; init; }
    public string Currency { get; init; } = "EUR";
    public string? DiscountCode { get; init; }
    public decimal? DiscountAmount { get; init; }
    public decimal DonationAmount { get; init; }
    public decimal VatAmount { get; init; }
    public string? PaymentMethod { get; init; }
    public string? PaymentMethodDetail { get; init; }
    public decimal? StripeFee { get; init; }
    public decimal? ApplicationFee { get; init; }
    public TicketPaymentStatus PaymentStatus { get; init; }
    public string? VendorDashboardUrl { get; init; }
    public Guid? MatchedUserId { get; init; }
    public string? MatchedUserName { get; init; }
}

/// <summary>Paged result of order rows.</summary>
public class OrdersPageResult
{
    public List<OrderRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

/// <summary>A single row in the attendees list page.</summary>
public class AttendeeRow
{
    public Guid Id { get; init; }
    public string AttendeeName { get; init; } = string.Empty;
    public string? AttendeeEmail { get; init; }
    public string TicketTypeName { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public TicketAttendeeStatus Status { get; init; }
    public Guid? MatchedUserId { get; init; }
    public string? MatchedUserName { get; init; }
    public string VendorOrderId { get; init; } = string.Empty;
}

/// <summary>Paged result of attendee rows.</summary>
public class AttendeesPageResult
{
    public List<AttendeeRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

/// <summary>A single row in the "who hasn't bought" page.</summary>
public class WhoHasntBoughtRowDto
{
    public Guid UserId { get; init; }
    public bool HasTicket { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Teams { get; init; } = string.Empty;
    public MembershipTier Tier { get; init; }
}

/// <summary>Paged result + metadata for the "who hasn't bought" page.</summary>
public class WhoHasntBoughtResult
{
    public List<WhoHasntBoughtRowDto> Humans { get; init; } = [];
    public int TotalCount { get; init; }
    public List<string> AvailableTeams { get; init; } = [];
}

/// <summary>CSV export data for attendees.</summary>
public class AttendeeExportRow
{
    public string AttendeeName { get; init; } = string.Empty;
    public string? AttendeeEmail { get; init; }
    public string TicketTypeName { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string Status { get; init; } = string.Empty;
    public string VendorOrderId { get; init; } = string.Empty;
}

/// <summary>CSV export data for orders.</summary>
public class OrderExportRow
{
    public string Date { get; init; } = string.Empty;
    public string BuyerName { get; init; } = string.Empty;
    public string BuyerEmail { get; init; } = string.Empty;
    public int AttendeeCount { get; init; }
    public decimal TotalAmount { get; init; }
    public string Currency { get; init; } = "EUR";
    public string? DiscountCode { get; init; }
    public decimal? DiscountAmount { get; init; }
    public decimal DonationAmount { get; init; }
    public decimal VatAmount { get; init; }
    public string? PaymentMethod { get; init; }
    public decimal? StripeFee { get; init; }
    public decimal? ApplicationFee { get; init; }
    public string PaymentStatus { get; init; } = string.Empty;
}

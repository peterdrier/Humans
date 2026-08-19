using Humans.Users.Contracts;
using NodaTime;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain;

namespace Humans.Tickets.Services.Dtos;

/// <summary>Aggregated ticket dashboard statistics.</summary>
internal sealed class TicketDashboardStats
{
    public int TicketsSold { get; init; }
    public decimal Revenue { get; init; }
    public decimal TotalStripeFees { get; init; }
    public decimal TotalApplicationFees { get; init; }
    public decimal NetRevenue { get; init; }
    public decimal AveragePrice { get; init; }
    public decimal GrossAveragePrice { get; init; }
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

internal sealed class FeeBreakdownByMethod
{
    public string PaymentMethod { get; init; } = string.Empty;
    public int OrderCount { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal TotalStripeFees { get; init; }
    public decimal TotalApplicationFees { get; init; }
    public decimal EffectiveRate { get; init; }
}

internal sealed class DailySales
{
    public string Date { get; init; } = string.Empty;
    public int TicketsSold { get; init; }
    public decimal? RollingAverage { get; init; }
}

internal sealed class RecentOrder
{
    public Guid Id { get; init; }
    public string BuyerName { get; init; } = string.Empty;
    public int TicketCount { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "EUR";
    public Instant PurchasedAt { get; init; }
    public bool IsMatched { get; init; }
    public TicketPaymentStatus PaymentStatus { get; init; }
}

/// <summary>Weekly and quarterly sales aggregates for reporting.</summary>
internal sealed class TicketSalesAggregates
{
    public List<WeeklySalesAggregate> WeeklySales { get; init; } = [];
    public List<QuarterlySalesAggregate> QuarterlySales { get; init; } = [];
    public List<TicketTypeSalesAggregate> ByTicketType { get; init; } = [];
    public List<DiscountCampaignAggregate> ByDiscountCampaign { get; init; } = [];
}

internal sealed class WeeklySalesAggregate
{
    public string WeekLabel { get; init; } = string.Empty;
    public int TicketsSold { get; init; }
    public decimal GrossRevenue { get; init; }
    public int OrderCount { get; init; }
    public decimal Donations { get; init; }
    public decimal VatAmount { get; init; }
    public decimal VipDonations { get; init; }
}

internal sealed class QuarterlySalesAggregate
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

internal sealed class TicketTypeSalesAggregate
{
    public string TicketTypeName { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public int TicketsSold { get; init; }

    /// <summary>Sum of listed ticket prices — not order revenue (order-level
    /// discounts and donations are excluded), so it won't reconcile with
    /// the weekly/quarterly GrossRevenue figures.</summary>
    public decimal FaceValue { get; init; }
}

/// <summary>
/// Discount-code usage and cost for one campaign. Grants come from the
/// Campaigns section; the money comes from the paid orders whose
/// <c>DiscountCode</c> matches a grant of that campaign. Codes redeemed on
/// paid orders that match no grant land in a single unattributed row with
/// <see cref="CodesGranted"/> null.
/// </summary>
internal sealed class DiscountCampaignAggregate
{
    public string CampaignTitle { get; init; } = string.Empty;

    /// <summary>Null for the unattributed row — nothing granted those codes.</summary>
    public int? CodesGranted { get; init; }

    /// <summary>Paid orders that carried one of this campaign's codes.</summary>
    public int CodesUsed { get; init; }

    public decimal AverageDiscount { get; init; }
    public decimal TotalDiscount { get; init; }
}

/// <summary>Aggregated code tracking data: campaign summaries + individual code details.</summary>
internal sealed class CodeTrackingData
{
    public int TotalCodesSent { get; init; }
    public int CodesRedeemed { get; init; }
    public int CodesUnused { get; init; }
    public decimal RedemptionRate { get; init; }
    public List<CampaignCodeSummaryDto> Campaigns { get; init; } = [];
    public List<CodeDetailDto> Codes { get; init; } = [];
}

internal sealed class CampaignCodeSummaryDto
{
    public Guid CampaignId { get; init; }
    public string CampaignTitle { get; init; } = string.Empty;
    public int TotalGrants { get; init; }
    public int Redeemed { get; init; }
    public int Unused { get; init; }
    public decimal RedemptionRate { get; init; }
}

internal sealed class CodeDetailDto
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

/// <summary>Break-even calculation result.</summary>
internal sealed class BreakEvenResult
{
    public int Target { get; init; }
    public string? Detail { get; init; }
    public string Currency { get; init; } = "EUR";
}

/// <summary>A single row in the orders list page.</summary>
internal sealed class OrderRow
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
internal sealed class OrdersPageResult
{
    public List<OrderRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

/// <summary>A single row in the attendees list page.</summary>
internal sealed class AttendeeRow
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
internal sealed class AttendeesPageResult
{
    public List<AttendeeRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

/// <summary>A single row in the "who hasn't bought" page.</summary>
internal sealed class WhoHasntBoughtRowDto
{
    public Guid UserId { get; init; }
    public bool HasTicket { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Teams { get; init; } = string.Empty;
    public MembershipTier Tier { get; init; }
}

/// <summary>Paged result + metadata for the "who hasn't bought" page.</summary>
internal sealed class WhoHasntBoughtResult
{
    public List<WhoHasntBoughtRowDto> Humans { get; init; } = [];
    public int TotalCount { get; init; }
    public List<string> AvailableTeams { get; init; } = [];
}

/// <summary>CSV export data for attendees.</summary>
internal sealed class AttendeeExportRow
{
    public string AttendeeName { get; init; } = string.Empty;
    public string? AttendeeEmail { get; init; }
    public string TicketTypeName { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string Status { get; init; } = string.Empty;
    public string VendorOrderId { get; init; } = string.Empty;
}

/// <summary>
/// Server-side aggregate numerics for the ticket dashboard (tickets sold,
/// gross revenue / fees across paid orders, unmatched order count).
/// </summary>
internal sealed class TicketDashboardTotals
{
    public int TicketsSold { get; init; }
    public decimal GrossRevenue { get; init; }
    public decimal TotalStripeFees { get; init; }
    public decimal TotalApplicationFees { get; init; }
    public int UnmatchedOrderCount { get; init; }
}

/// <summary>
/// A paid order's payment-method and fee slice, used for in-memory grouping
/// by payment method on the ticket dashboard.
/// </summary>
internal sealed class PaidOrderPaymentMethodRow
{
    public string? PaymentMethod { get; init; }
    public string? PaymentMethodDetail { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal? StripeFee { get; init; }
    public decimal? ApplicationFee { get; init; }
}

/// <summary>
/// Narrow per-order projection used by the dashboard's daily-sales chart:
/// purchase instant and the count of attendees on the order.
/// </summary>
internal sealed class OrderDateAndCount
{
    public Instant PurchasedAt { get; init; }
    public int AttendeeCount { get; init; }
}

/// <summary>
/// Narrow per-paid-order projection used for weekly/quarterly sales
/// aggregation. <see cref="VipDonations"/> is the sum of
/// <c>(Price - VipThreshold)</c> across Valid/CheckedIn VIP attendees priced
/// above the threshold.
/// </summary>
internal sealed class PaidOrderSalesRow
{
    public Instant PurchasedAt { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal DonationAmount { get; init; }
    public decimal VatAmount { get; init; }
    public int AttendeeCount { get; init; }
    public decimal VipDonations { get; init; }
}

/// <summary>
/// Narrow per-attendee projection used to aggregate sales by ticket type and
/// price. One row per Valid/CheckedIn attendee on a paid order.
/// </summary>
internal sealed class PaidAttendeeTypePriceRow
{
    public string TicketTypeName { get; init; } = string.Empty;
    public decimal Price { get; init; }
}

/// <summary>
/// Narrow order projection used to correlate campaign-code redemptions to
/// their redeeming order buyer. One row per order that carries a discount
/// code.
/// </summary>
internal sealed class DiscountCodeOrderRow
{
    public string DiscountCode { get; init; } = string.Empty;
    public string BuyerName { get; init; } = string.Empty;
    public string BuyerEmail { get; init; } = string.Empty;
    public string VendorOrderId { get; init; } = string.Empty;
    public decimal? DiscountAmount { get; init; }
    public bool IsPaid { get; init; }
}

/// <summary>CSV export data for orders.</summary>
internal sealed class OrderExportRow
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

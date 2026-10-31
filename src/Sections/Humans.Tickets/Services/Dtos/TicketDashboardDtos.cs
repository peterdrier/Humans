using Humans.Governance.Contracts;
using System.Text.Json.Serialization;
using NodaTime;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain;

namespace Humans.Tickets.Services.Dtos;

internal sealed class TicketDashboardStats
{
    public int TicketsSold { get; init; }
    public decimal Revenue { get; init; }
    public decimal TotalStripeFees { get; init; }
    public decimal TotalApplicationFees { get; init; }
    public decimal TotalFeeTax { get; init; }
    public decimal TotalWithheldTax { get; init; }
    public decimal NetRevenue { get; init; }
    public decimal GrossAveragePrice { get; init; }
    public int UnmatchedOrderCount { get; init; }

    public List<FeeBreakdownByMethod> FeesByPaymentMethod { get; init; } = [];
    public List<DailySales> DailySalesPoints { get; init; } = [];
    public List<RecentOrder> RecentOrders { get; init; } = [];

    public TicketSyncStatus SyncStatus { get; init; }
    public string? SyncError { get; init; }
    public Instant? LastSyncAt { get; init; }
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

internal sealed class TicketSalesAggregates
{
    public List<WeeklySalesAggregate> WeeklySales { get; init; } = [];
    public List<QuarterlySalesAggregate> QuarterlySales { get; init; } = [];
    public List<MonthlySalesAggregate> MonthlySales { get; init; } = [];
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

/// <summary>
/// <see cref="RefundedGross"/> is the gross of orders since refunded, by purchase month, so the row still ties to the
/// processor statement. A month with only refunds still gets a row.
/// </summary>
internal sealed class MonthlySalesAggregate
{
    /// <summary>ISO year-month, e.g. "2026-03".</summary>
    public string MonthLabel { get; init; } = string.Empty;
    public int OrderCount { get; init; }
    public int TicketsSold { get; init; }
    public decimal GrossRevenue { get; init; }
    public decimal Donations { get; init; }
    public decimal VipDonations { get; init; }
    public decimal VatAmount { get; init; }
    public decimal StripeFees { get; init; }
    public decimal ApplicationFees { get; init; }
    /// <summary>VAT Stripe charged on its fees.</summary>
    public decimal FeeTax { get; init; }
    /// <summary>Tax Stripe withheld from the payout.</summary>
    public decimal WithheldTax { get; init; }
    public decimal RefundedGross { get; init; }

    /// <summary>
    /// Built per order the way the VAT base is (Σ min(price, VIP threshold) over live seats less the discount,
    /// floored at zero), so it is the base <see cref="VatAmount"/> was charged on.
    /// </summary>
    public decimal TicketIncomeInclVat { get; init; }

    public decimal TicketIncomeExVat => TicketIncomeInclVat - VatAmount;
}

internal sealed class TicketTypeSalesAggregate
{
    public string TicketTypeName { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public int TicketsSold { get; init; }

    /// <summary>Sum of listed ticket prices, without order discounts and donations, so it won't reconcile with
    /// GrossRevenue.</summary>
    public decimal FaceValue { get; init; }
}

/// <summary>
/// Codes redeemed on paid orders that match no grant land in one unattributed row with <see cref="CodesGranted"/> null.
/// </summary>
internal sealed class DiscountCampaignAggregate
{
    public string CampaignTitle { get; init; } = string.Empty;
    public Instant CreatedAt { get; init; }

    /// <summary>Null for the unattributed row — nothing granted those codes.</summary>
    public int? CodesGranted { get; init; }

    public int CodesUsed { get; init; }

    public decimal AverageDiscount { get; init; }
    public decimal TotalDiscount { get; init; }
}

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
    public Instant CreatedAt { get; init; }
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

internal sealed class BreakEvenResult
{
    public int Target { get; init; }
    public string? Detail { get; init; }
    public string Currency { get; init; } = "EUR";
}

[method: JsonConstructor]
internal sealed record OrderRow()
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

internal sealed class OrdersPageResult
{
    public List<OrderRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

[method: JsonConstructor]
internal sealed record AttendeeRow()
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

internal sealed class AttendeesPageResult
{
    public List<AttendeeRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

internal sealed class WhoHasntBoughtRowDto
{
    public Guid UserId { get; init; }
    public bool HasTicket { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Teams { get; init; } = string.Empty;
    public MembershipTier Tier { get; init; }
}

internal sealed class WhoHasntBoughtResult
{
    public List<WhoHasntBoughtRowDto> Humans { get; init; } = [];
    public int TotalCount { get; init; }
    public List<string> AvailableTeams { get; init; } = [];
}

internal sealed class AttendeeExportRow
{
    public string AttendeeName { get; init; } = string.Empty;
    public string? AttendeeEmail { get; init; }
    public string TicketTypeName { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string Status { get; init; } = string.Empty;
    public string VendorOrderId { get; init; } = string.Empty;
}

internal sealed class TicketDashboardTotals
{
    public int TicketsSold { get; init; }
    public decimal GrossRevenue { get; init; }
    public decimal TotalStripeFees { get; init; }
    public decimal TotalApplicationFees { get; init; }
    public decimal TotalFeeTax { get; init; }
    public decimal TotalWithheldTax { get; init; }
    public int UnmatchedOrderCount { get; init; }
}

internal sealed class PaidOrderPaymentMethodRow
{
    public string? PaymentMethod { get; init; }
    public string? PaymentMethodDetail { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal? StripeFee { get; init; }
    public decimal? ApplicationFee { get; init; }
}

internal sealed class OrderDateAndCount
{
    public Instant PurchasedAt { get; init; }
    public int AttendeeCount { get; init; }
}

internal sealed class PaidOrderSalesRow
{
    public Instant PurchasedAt { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal DonationAmount { get; init; }
    public decimal VatAmount { get; init; }
    public int AttendeeCount { get; init; }
    public decimal VipDonations { get; init; }

    /// <summary>
    /// The seats the VAT base is taken over. Not derivable from <see cref="TotalAmount"/>: a transfer voids a seat
    /// and adds a live replacement at the same price on the same order.
    /// </summary>
    public decimal LiveSeatGross { get; init; }
    public decimal DiscountAmount { get; init; }
    public decimal? StripeFee { get; init; }
    public decimal? ApplicationFee { get; init; }
    public decimal? FeeTax { get; init; }
    public decimal? WithheldTax { get; init; }
}

/// <summary>A refund does not return the fees already charged, so they still belong in the monthly totals.</summary>
internal sealed class RefundedOrderRow
{
    public Instant PurchasedAt { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal? StripeFee { get; init; }
    public decimal? ApplicationFee { get; init; }
    public decimal? FeeTax { get; init; }
    public decimal? WithheldTax { get; init; }
}

internal sealed class PaidAttendeeTypePriceRow
{
    public string TicketTypeName { get; init; } = string.Empty;
    public decimal Price { get; init; }
}

internal sealed class DiscountCodeOrderRow
{
    public string DiscountCode { get; init; } = string.Empty;
    public string BuyerName { get; init; } = string.Empty;
    public string BuyerEmail { get; init; } = string.Empty;
    public string VendorOrderId { get; init; } = string.Empty;
    public decimal? DiscountAmount { get; init; }
    public bool IsPaid { get; init; }
}

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
    public decimal? FeeTax { get; init; }
    public decimal? WithheldTax { get; init; }
    public string PaymentStatus { get; init; } = string.Empty;
    public string VendorOrderId { get; init; } = string.Empty;
    public Instant PurchasedAt { get; init; }

    /// <summary>Sum of the amount above the VIP threshold over the order's live seats.</summary>
    public decimal VipDonations { get; init; }
}

/// <summary><see cref="Date"/> is in Europe/Madrid, the zone the monthly accountant report buckets by.</summary>
internal sealed record DonationExportRow(
    string Date,
    string VendorOrderId,
    string BuyerName,
    string BuyerEmail,
    string Type,
    decimal Amount)
{
    public const string VipTicket = "VIP ticket";
    public const string SeparateDonation = "Separate donation";
}

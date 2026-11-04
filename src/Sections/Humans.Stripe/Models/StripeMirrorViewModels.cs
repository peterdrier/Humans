using NodaTime;

namespace Humans.Stripe.Models;

internal sealed record StripeOption(string Value, string Label);

internal sealed record StripeSyncInfo(string Status, Instant? LastSyncAt, string? LastError);

internal sealed record StripeMoney(string Currency, decimal Available, decimal Pending)
{
    public decimal Total => Available + Pending;
}

internal sealed record StripeHeldPoint(LocalDate Date, decimal Held, decimal Change);

/// <summary>Amounts in EUR. <c>Computed</c> is the sum of every mirrored transaction's net; with a full mirror it
/// equals Stripe's available plus pending.</summary>
internal sealed record StripeAccountSummary(
    string Id,
    string Name,
    bool IsConfigured,
    bool UsesOrgKey,
    StripeSyncInfo? Sync,
    int TransactionCount,
    int PayoutCount,
    IReadOnlyList<StripeMoney> Balances,
    decimal Computed,
    decimal? Difference,
    decimal Gross,
    decimal Fees,
    decimal Refunds,
    decimal PaidOut,
    decimal ReserveHeldNow,
    StripeHeldPoint? ReservePeak,
    StripeHeldPoint? MinimumKept,
    IReadOnlyList<string> OtherCurrencies);

internal sealed record StripeSummary(bool AnyConfigured, IReadOnlyList<StripeAccountSummary> Accounts);

internal sealed record StripeTransactionsFilter(string? Account, string? Type, string? Q, string? Payout);

internal sealed record StripeTransactionRow(
    string Id,
    string AccountName,
    Instant Created,
    LocalDate AvailableOn,
    string Type,
    string ReportingCategory,
    string Status,
    decimal Amount,
    decimal Fee,
    decimal Net,
    string Currency,
    string? Description,
    string? CustomerName,
    string? CustomerEmail,
    string? SourceId,
    string? PayoutId);

internal sealed record StripeTransactionsPage(
    StripeTransactionsFilter Filter,
    IReadOnlyList<StripeTransactionRow> Rows,
    IReadOnlyList<StripeOption> Accounts,
    IReadOnlyList<string> Types);

internal sealed record StripeTransactionView(
    StripeTransactionRow Row,
    string AccountId,
    string? PaymentIntentId,
    string? Metadata,
    string Raw);

internal sealed record StripePayoutRow(
    string Id,
    string AccountName,
    Instant Created,
    LocalDate ArrivalDate,
    decimal Amount,
    string Currency,
    string Status,
    bool Automatic,
    string? Method,
    string? FailureMessage,
    int LinkedCount,
    decimal LinkedNet)
{
    /// <summary>A paid automatic payout whose linked rows do not net to its amount.</summary>
    public bool LinkedMismatch => Automatic && string.Equals(Status, "paid", StringComparison.Ordinal) && LinkedNet != Amount;
}

internal sealed record StripePayoutsPage(string? Account, IReadOnlyList<StripePayoutRow> Rows, IReadOnlyList<StripeOption> Accounts);

internal sealed record StripeHoldCurve(string AccountName, string Label, IReadOnlyList<StripeHeldPoint> Points, decimal Peak);

internal sealed record StripeReservesPage(IReadOnlyList<StripeHoldCurve> Curves);

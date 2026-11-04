using NodaTime;

namespace Humans.Pleo.Models;

internal sealed record PleoSyncInfo(
    string Status, Instant? LastSyncAt, int LastCount, int SkippedCount, string? LastError);

/// <summary>One family's line in the reconciliation strip. <see cref="Total"/> is signed (+ in, − out) and net of
/// refunds; refunds are rows whose sign runs against their family.</summary>
internal sealed record PleoKindTotal(string Label, int Count, decimal Total, int RefundCount, decimal RefundTotal);

/// <summary><c>Computed</c> is Σ signed EUR of every completed, classified entry; <c>PleoCurrent</c> is Pleo's own
/// current balance, which also leaves pending debits out.</summary>
internal sealed record PleoReconciliation(
    IReadOnlyList<PleoKindTotal> Kinds,
    decimal Computed,
    decimal? PleoCurrent,
    decimal? PleoPendingDebits,
    int ConvertedCount,
    int NoEurCount,
    int PendingCount,
    int UnclassifiedCount,
    decimal UnclassifiedTotal)
{
    public decimal? Difference => PleoCurrent - Computed;
}

/// <summary>Spend is positive (money out), net of refunds.</summary>
internal sealed record PleoSpendTotal(string Key, string Label, int Count, decimal Spend, bool Flagged = false);

internal sealed record PleoSummary(
    bool IsConfigured,
    PleoSyncInfo? Sync,
    int EntryCount,
    PleoReconciliation Reconciliation,
    IReadOnlyList<PleoSpendTotal> ByAccount,
    PleoSpendTotal Unmapped,
    PleoSpendTotal MissingReceipt,
    IReadOnlyList<PleoSpendTotal> ByCardholder,
    int UnlinkedCategoryCount);

internal sealed record PleoEntryRow(
    string Id,
    Instant? PerformedAt,
    Instant? SettledAt,
    string Cardholder,
    string? Merchant,
    string? Family,
    string? SubFamily,
    string? Status,
    decimal? AmountEur,
    string Currency,
    long Minors,
    string? HoldedCode,
    string? HoldedName,
    bool HasReceipt);

internal sealed record PleoEntriesFilter(
    string? Account, string? Cardholder, string? Family, bool NoReceipt, bool Unmapped);

internal sealed record PleoOption(string Value, string Label);

internal sealed record PleoEntriesPage(
    PleoEntriesFilter Filter,
    IReadOnlyList<PleoEntryRow> Rows,
    IReadOnlyList<PleoOption> Accounts,
    IReadOnlyList<PleoOption> Cardholders,
    IReadOnlyList<string> Families);

internal sealed record PleoReceiptView(string Id, string? MimeType);

internal sealed record PleoEntryView(
    PleoEntryRow Row,
    string? CategoryId,
    string? CategoryName,
    string? Note,
    string RawJson,
    IReadOnlyList<PleoReceiptView> Receipts,
    string? ReceiptError);

internal sealed record PleoUnlinkedCategory(
    string CategoryId, int Count, decimal Spend, IReadOnlyList<string> TopMerchants, IReadOnlyList<string> Cardholders);

internal sealed record PleoCategoryRow(
    Guid Id,
    string Name,
    string HoldedCode,
    string? HoldedName,
    bool CodeProblem,
    string? PleoCategoryId,
    int Count,
    decimal Spend,
    Instant? SetAt);

internal sealed record PleoCategoriesPage(
    IReadOnlyList<PleoUnlinkedCategory> Unlinked,
    IReadOnlyList<PleoCategoryRow> Categories,
    IReadOnlyList<PleoOption> UnlinkedNames,
    IReadOnlyList<PleoOption> ActiveAccounts);

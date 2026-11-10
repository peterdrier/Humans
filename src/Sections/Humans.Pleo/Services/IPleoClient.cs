using System.Text.Json;

namespace Humans.Pleo.Services;

/// <summary>Read-only: GETs and the <c>:search</c> POSTs. The key carries write scopes; nothing here uses them.</summary>
internal interface IPleoClient
{
    /// <summary>False without <c>PLEO_API_KEY</c> or <c>PLEO_COMPANY_ID</c>.</summary>
    bool IsConfigured { get; }

    /// <summary>Every accounting entry, raw, across all pages.</summary>
    Task<IReadOnlyList<JsonElement>> SearchEntriesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PleoAccountWire>> SearchAccountsAsync(CancellationToken ct = default);
    /// <summary>Active and deleted cardholders, so historical entries retain their names.</summary>
    Task<IReadOnlyList<PleoEmployeeWire>> ListEmployeesAsync(CancellationToken ct = default);
    Task<PleoBalanceWire> GetBalanceAsync(CancellationToken ct = default);

    /// <summary>The entry's receipts, each with a short-lived signed download URL.</summary>
    Task<IReadOnlyList<PleoReceiptWire>> ListReceiptsAsync(string entryId, CancellationToken ct = default);

    /// <summary>One receipt file, fetched live from Pleo's CDN and never stored.</summary>
    Task<PleoReceiptFile> GetReceiptFileAsync(string entryId, string receiptId, CancellationToken ct = default);
}

internal sealed record PleoMoneyWire(string? Currency, long? Minors);

internal sealed record PleoMerchantWire(string? Name, string? MerchantCategoryCode, PleoAddressWire? Address);

internal sealed record PleoAddressWire(string? Country);

internal sealed record PleoEntryWire(
    string? Id,
    string? EmployeeId,
    string? TeamId,
    string? AccountId,
    string? Family,
    string? SubFamily,
    string? Status,
    string? ExportStatus,
    string? ReviewStatus,
    DateTimeOffset? PerformedAt,
    DateTimeOffset? SettledAt,
    DateTimeOffset? BookkeepingDate,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    PleoMoneyWire? TransactionValue,
    PleoMoneyWire? TotalBillValue,
    PleoMerchantWire? Merchant,
    string? Note,
    IReadOnlyList<string>? ReceiptIds);

internal sealed record PleoAccountWire(string Id, string? Code, string? Name, string? ExternalId, bool Archived);

internal sealed record PleoEmployeeWire(string Id, string? FirstName, string? LastName, string? Code, bool IsDeleted);

internal sealed record PleoBalanceWire(string Currency, long Current, long Available, long PendingDebits);

internal sealed record PleoReceiptWire(string Id, string? MimeType, string? Url);

internal sealed record PleoReceiptFile(byte[] Content, string ContentType);

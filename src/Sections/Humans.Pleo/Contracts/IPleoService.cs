using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Pleo.Contracts;

/// <summary>Read-only view of the Pleo mirror, for booking it elsewhere. Served from the mirror; no Pleo calls.</summary>
public interface IPleoService : IApplicationService
{
    /// <summary>Entries settled in [<paramref name="from"/>, <paramref name="to"/>] (org-zone dates, inclusive).
    /// Entries Pleo has not settled are not returned.</summary>
    Task<IReadOnlyList<PleoEntryInfo>> GetSettledEntriesAsync(
        LocalDate from, LocalDate to, CancellationToken ct = default);

    /// <summary>The wallet's current balance at the last sweep, in EUR; null before the first sweep or when Pleo
    /// reports another currency. Pending debits are left out, as in Pleo's own figure.</summary>
    Task<PleoWalletBalance?> GetWalletBalanceAsync(CancellationToken ct = default);

    /// <summary>One receipt of a mirrored entry, fetched live from Pleo. Null when the entry is not mirrored, Pleo is
    /// not configured, or the file can't be fetched (logged).</summary>
    Task<PleoReceiptFile?> GetReceiptFileAsync(string entryId, string receiptId, CancellationToken ct = default);
}

/// <summary><paramref name="ContentType"/> is an image type, <c>application/pdf</c>, or
/// <c>application/octet-stream</c> for anything else.</summary>
public sealed record PleoReceiptFile(byte[] Content, string ContentType);

/// <summary><paramref name="SyncedAt"/> is when the sweep that read it ran.</summary>
public sealed record PleoWalletBalance(decimal CurrentEur, Instant SyncedAt);

/// <summary>One mirrored entry. <c>AmountEur</c> is signed (+ into the Pleo wallet, − out), null when Pleo gave no EUR
/// figure. <c>HoldedCode</c> is the Holded account the entry's category maps to, null when the entry has no category
/// or its category id is not linked yet. <c>HasReceipt</c> is whether Pleo holds a receipt or invoice for it. An entry
/// without a code is never booked onto a guessed account. <c>Note</c> is the Pleo note, <c>Cardholder</c> the cardholder's name, and
/// <c>ReceiptIds</c> the receipts Pleo listed at the last sweep (empty when none).</summary>
public sealed record PleoEntryInfo(
    string Id,
    string? Family,
    string? Status,
    Instant SettledAt,
    decimal? AmountEur,
    string? MerchantName,
    string? CategoryId,
    string? HoldedCode,
    bool HasReceipt,
    string? Note = null,
    string? Cardholder = null,
    IReadOnlyList<string>? ReceiptIds = null);

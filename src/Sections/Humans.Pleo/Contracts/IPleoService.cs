using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Pleo.Contracts;

/// <summary>Read-only view of the Pleo mirror, for booking it elsewhere. Served from the mirror; no Pleo calls.</summary>
public interface IPleoService : IApplicationService
{
    /// <summary>Entries settled in [<paramref name="from"/>, <paramref name="to"/>] (Madrid dates, inclusive).
    /// Entries Pleo has not settled are not returned.</summary>
    Task<IReadOnlyList<PleoEntryInfo>> GetSettledEntriesAsync(
        LocalDate from, LocalDate to, CancellationToken ct = default);
}

/// <summary>One mirrored entry. <c>AmountEur</c> is signed (+ into the Pleo wallet, − out), null when Pleo gave no EUR
/// figure. <c>HoldedCode</c> is the Holded account the entry's category maps to, null when the entry has no category
/// or its category id is not linked yet. <c>HasReceipt</c> is whether Pleo holds a receipt or invoice for it. Only an
/// entry with both a code and a receipt may be booked; anything else waits, never guessed onto an account and never
/// booked without its document.</summary>
public sealed record PleoEntryInfo(
    string Id,
    string? Family,
    string? SubFamily,
    string? Status,
    Instant SettledAt,
    string Currency,
    long Minors,
    decimal? AmountEur,
    string? MerchantName,
    string? EmployeeId,
    string? CategoryId,
    string? HoldedCode,
    bool HasReceipt);

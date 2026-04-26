using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// A Holded purchase invoice synced into Humans for budget reconciliation.
/// Stored verbatim alongside a persisted MatchStatus so the unmatched-queue
/// page is a simple WHERE.
/// </summary>
public class HoldedTransaction
{
    public Guid Id { get; init; }

    /// <summary>Holded's id (24-char hex). Natural key for upsert.</summary>
    public string HoldedDocId { get; set; } = string.Empty;

    public string HoldedDocNumber { get; set; } = string.Empty;
    public string ContactName { get; set; } = string.Empty;

    public LocalDate Date { get; set; }
    public LocalDate? AccountingDate { get; set; }
    public LocalDate? DueDate { get; set; }

    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }

    public decimal PaymentsTotal { get; set; }
    public decimal PaymentsPending { get; set; }
    public decimal PaymentsRefunds { get; set; }

    public string Currency { get; set; } = "eur";
    public Instant? ApprovedAt { get; set; }

    /// <summary>Raw tags array from Holded, JSON-serialized.</summary>
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    /// <summary>Full Holded JSON for debugging + future field needs.</summary>
    public string RawPayload { get; set; } = "{}";

    /// <summary>from.id when from.docType = "incomingdocument"; deep-link to original receipt.</summary>
    public string? SourceIncomingDocId { get; set; }

    /// <summary>Matched BudgetCategory (FK only, no nav). Null when unmatched.</summary>
    public Guid? BudgetCategoryId { get; set; }

    public HoldedMatchStatus MatchStatus { get; set; }

    public Instant LastSyncedAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}

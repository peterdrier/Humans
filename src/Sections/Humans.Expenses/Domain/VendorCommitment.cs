using NodaTime;

namespace Humans.Expenses.Domain;

/// <summary>
/// A promise to pay a vendor, recorded the moment a quote or proforma is accepted — before any
/// money leaves (nobodies-collective/Humans#1030). Holded only ever holds real invoices; this is
/// the pre-accounting layer Holded lacks.
/// </summary>
internal sealed class VendorCommitment
{
    public Guid Id { get; init; }
    public string VendorName { get; set; } = "";
    public decimal ExpectedAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Purpose { get; set; } = "";
    /// <summary>Budget line this commitment charges. Budget's key, held as a bare Guid — Expenses'
    /// EF model joins only to its own tables (memory/architecture/no-cross-section-ef-joins.md).</summary>
    public Guid? BudgetCategoryId { get; set; }
    public VendorCommitmentStatus Status { get; set; }
    /// <summary>The accepted quote/proforma. Deliberately NOT an <c>expense_attachments</c> row:
    /// that table is member-scoped data on the GDPR export path, and a vendor's quote is the
    /// organisation's document, not any member's. One file per commitment, keyed by its id.</summary>
    public string? QuoteFileName { get; set; }
    public string? QuoteContentType { get; set; }
    public string? QuoteExtension { get; set; }
    public Instant? QuoteUploadedAt { get; set; }
    /// <summary>The Holded purchase document this commitment resolved to, once matched.</summary>
    public string? MatchedHoldedDocId { get; set; }
    public string? MatchedHoldedDocNumber { get; set; }
    public Instant? MatchedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
    public Instant? ClosedAt { get; set; }

    public ICollection<VendorCommitmentPayment> Payments { get; set; } = new List<VendorCommitmentPayment>();
    public ICollection<VendorCommitmentMatchCandidate> MatchCandidates { get; set; }
        = new List<VendorCommitmentMatchCandidate>();
}

using Humans.Expenses.Domain;
using NodaTime;

namespace Humans.Expenses.Services.Dtos;

/// <summary>One recorded payment out against a commitment.</summary>
internal sealed record VendorCommitmentPaymentDto(
    Guid Id,
    decimal Amount,
    LocalDate PaidOn,
    string? Reference,
    Guid RecordedByUserId,
    Instant CreatedAt);

/// <summary>A purchase document parked for a human decision — ambiguous fit, or a suspected dupe.</summary>
internal sealed record VendorCommitmentMatchCandidateDto(
    Guid Id,
    Guid VendorCommitmentId,
    string HoldedDocId,
    string HoldedDocNumber,
    string ContactName,
    LocalDate DocDate,
    decimal DocTotal,
    VendorCommitmentMatchKind Kind,
    Instant DetectedAt,
    bool? Accepted,
    Instant? ResolvedAt);

/// <summary>
/// The canonical commitment read shape. <see cref="TotalPaid"/> is derived from
/// <see cref="Payments"/> rather than stored, so it can never drift from the rows.
/// </summary>
internal sealed record VendorCommitmentDto
{
    public required Guid Id { get; init; }
    public required string VendorName { get; init; }
    public required decimal ExpectedAmount { get; init; }
    public required string Currency { get; init; }
    public required string Purpose { get; init; }
    public Guid? BudgetCategoryId { get; init; }
    public required VendorCommitmentStatus Status { get; init; }
    public string? QuoteFileName { get; init; }
    public string? QuoteContentType { get; init; }
    public string? QuoteExtension { get; init; }
    public Instant? QuoteUploadedAt { get; init; }
    public string? MatchedHoldedDocId { get; init; }
    public string? MatchedHoldedDocNumber { get; init; }
    public Instant? MatchedAt { get; init; }
    public required Guid CreatedByUserId { get; init; }
    public required Instant CreatedAt { get; init; }
    public required Instant UpdatedAt { get; init; }
    public Instant? ClosedAt { get; init; }

    public IReadOnlyList<VendorCommitmentPaymentDto> Payments { get; init; } = [];
    public IReadOnlyList<VendorCommitmentMatchCandidateDto> MatchCandidates { get; init; } = [];

    public decimal TotalPaid => Payments.Sum(p => p.Amount);

    /// <summary>The liability: money has gone out and no purchase document backs it yet (AC2).
    /// A Closed commitment is out: closing is the accountant saying the matter is settled, and a
    /// list you cannot ever clear is not a liability list.</summary>
    public bool IsPaidAwaitingInvoice =>
        TotalPaid > 0m && MatchedHoldedDocId is null && Status != VendorCommitmentStatus.Closed;

    public bool CanRecordPayment => Status != VendorCommitmentStatus.Closed;

    /// <summary>
    /// Two ways out of the registry: the invoice arrived and the cost is booked, or the quote was
    /// never taken up and no money moved. Anything else still owes someone an invoice. One
    /// definition, read by both the service guard and the screen that offers the button.
    /// </summary>
    public bool CanClose =>
        Status == VendorCommitmentStatus.Invoiced
        || (Status == VendorCommitmentStatus.Open && TotalPaid == 0m);

    /// <summary>Unresolved review rows, oldest first — the order they are worked in.</summary>
    public IReadOnlyList<VendorCommitmentMatchCandidateDto> PendingCandidates =>
        MatchCandidates.Where(c => c.ResolvedAt is null).OrderBy(c => c.DetectedAt).ToList();

    /// <summary>Payments in the order they left the account.</summary>
    public IReadOnlyList<VendorCommitmentPaymentDto> PaymentsByDate =>
        Payments.OrderBy(p => p.PaidOn).ThenBy(p => p.CreatedAt).ToList();
}

/// <summary>What one matcher run did, for the operator's confirmation message.</summary>
internal sealed record VendorCommitmentMatchRunResult(
    int Examined, int Linked, int Ambiguous, int Duplicates);

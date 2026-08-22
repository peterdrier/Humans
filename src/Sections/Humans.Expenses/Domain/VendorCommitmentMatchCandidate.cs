using NodaTime;

namespace Humans.Expenses.Domain;

/// <summary>
/// A Holded purchase document the matcher refused to link on its own — either because several
/// documents fit equally well, or because the commitment is already Invoiced and this would be a
/// second booking of the same cost. Resolved by a human, never by the matcher.
/// </summary>
internal sealed class VendorCommitmentMatchCandidate
{
    public Guid Id { get; init; }
    public Guid VendorCommitmentId { get; set; }
    public string HoldedDocId { get; set; } = "";
    public string HoldedDocNumber { get; set; } = "";
    public string ContactName { get; set; } = "";
    public LocalDate DocDate { get; set; }
    public decimal DocTotal { get; set; }
    public VendorCommitmentMatchKind Kind { get; set; }
    public Instant DetectedAt { get; set; }
    /// <summary>Null while pending; true when the human linked this document, false when dismissed.</summary>
    public bool? Accepted { get; set; }
    public Instant? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }

    public VendorCommitment? Commitment { get; set; }
}

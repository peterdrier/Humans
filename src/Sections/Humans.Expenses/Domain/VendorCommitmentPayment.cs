using NodaTime;

namespace Humans.Expenses.Domain;

/// <summary>Money that actually left, recorded against the commitment it was promised under.</summary>
internal sealed class VendorCommitmentPayment
{
    public Guid Id { get; init; }
    public Guid VendorCommitmentId { get; set; }
    public decimal Amount { get; set; }
    public LocalDate PaidOn { get; set; }
    /// <summary>Bank reference / transfer note, so the payment can be found on the statement.</summary>
    public string? Reference { get; set; }
    public Guid RecordedByUserId { get; set; }
    public Instant CreatedAt { get; init; }

    public VendorCommitment? Commitment { get; set; }
}

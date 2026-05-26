using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>Cached Holded payment row, keyed by contact for creditor settle detection.</summary>
public class HoldedPayment
{
    public Guid Id { get; init; }
    public string HoldedPaymentId { get; set; } = "";  // unique upsert key
    public string HoldedContactId { get; set; } = "";
    public decimal Amount { get; set; }
    public LocalDate Date { get; set; }
    public string? DocumentType { get; set; }
    public Instant LastSyncedAt { get; set; }
    public Instant CreatedAt { get; init; }
}

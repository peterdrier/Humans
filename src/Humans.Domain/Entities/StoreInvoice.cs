using NodaTime;

namespace Humans.Domain.Entities;

public class StoreInvoice
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string HoldedDocId { get; set; } = string.Empty;
    public string HoldedDocNumber { get; set; } = string.Empty;
    public Instant IssuedAt { get; set; }
    public Guid IssuedByUserId { get; set; }
    public string RequestPayload { get; set; } = string.Empty;
    public string ResponsePayload { get; set; } = string.Empty;
}

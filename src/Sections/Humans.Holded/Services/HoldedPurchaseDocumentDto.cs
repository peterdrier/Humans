using Humans.Holded.Contracts;
using NodaTime;

namespace Humans.Holded.Services;

internal sealed record HoldedPurchaseDocumentDto
{
    public Instant? ApprovedAt { get; init; }
    /// <summary>Null when blank. Only this single GET returns it; the v2 purchase list never does.</summary>
    public string? Notes { get; init; }
    /// <summary>In Holded's order. The only place a payment's id can be read: <c>POST purchases/{id}/payments</c>
    /// answers <c>{"status":"paid"}</c> and nothing else.</summary>
    public IReadOnlyList<HoldedPaymentDetailDto> Payments { get; init; } = [];
}

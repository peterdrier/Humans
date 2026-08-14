using Humans.Tickets.Contracts;
using NodaTime;

namespace Humans.Tickets.Services;

/// <summary>
/// The section's forwarding edge onto the Base vendor port: the one place in the
/// codebase that names both the application's ticketing vocabulary
/// (<see cref="TicketDiscountCodeRequest"/>, <see cref="TicketDiscountKind"/>) and the
/// port's (<c>DiscountCodeSpec</c>, <c>DiscountType</c>).
/// </summary>
/// <remarks>
/// Tickets is the application's only door to ticketing. A section that needs a discount
/// code or wants a gate admission mirrored asks <em>Tickets</em>, through
/// <see cref="ITicketDiscountCodes"/> / <see cref="ITicketVendorMirror"/> on the contracts
/// leaf, and Tickets asks the vendor. Keeping the map here is what lets the two
/// vocabularies drift: when the 2027 vendor's notion of a discount is not
/// percentage-or-fixed, this file absorbs it and neither Campaigns nor any later consumer
/// is recompiled (Finance's <c>CreditorLedgerLine</c> shape).
/// </remarks>
internal sealed class TicketVendorGateway(ITicketVendorService vendor)
    : ITicketDiscountCodes, ITicketVendorMirror
{
    public Task<IReadOnlyList<string>> GenerateAsync(
        TicketDiscountCodeRequest request, CancellationToken ct = default) =>
        vendor.GenerateDiscountCodesAsync(
            new DiscountCodeSpec(request.Count, ToVendorType(request.Kind), request.Value, request.ExpiresAt),
            ct);

    public Task CreateCheckInAsync(string vendorTicketId, Instant occurredAt, CancellationToken ct = default) =>
        vendor.CreateCheckInAsync(vendorTicketId, occurredAt, ct);

    private static DiscountType ToVendorType(TicketDiscountKind kind) => kind switch
    {
        TicketDiscountKind.Percentage => DiscountType.Percentage,
        TicketDiscountKind.Fixed => DiscountType.Fixed,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown discount kind."),
    };
}

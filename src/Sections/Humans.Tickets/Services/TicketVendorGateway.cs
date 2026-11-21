using Humans.Tickets.Contracts;

namespace Humans.Tickets.Services;

/// <summary>
/// The one place that names both the application's discount vocabulary and the vendor port's, so the two can drift
/// apart without recompiling any consumer.
/// </summary>
internal sealed class TicketVendorGateway(ITicketVendorService vendor)
    : ITicketDiscountCodes
{
    public Task<IReadOnlyList<string>> GenerateAsync(
        TicketDiscountCodeRequest request, CancellationToken ct = default) =>
        vendor.GenerateDiscountCodesAsync(
            new DiscountCodeSpec(request.Count, ToVendorType(request.Kind), request.Value, request.ExpiresAt),
            ct);

    private static DiscountType ToVendorType(TicketDiscountKind kind) => kind switch
    {
        TicketDiscountKind.Percentage => DiscountType.Percentage,
        TicketDiscountKind.Fixed => DiscountType.Fixed,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown discount kind."),
    };
}

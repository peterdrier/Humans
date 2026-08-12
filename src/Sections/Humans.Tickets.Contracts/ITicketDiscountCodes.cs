using Humans.Application.Architecture;
using Humans.Application.Interfaces;

namespace Humans.Tickets.Contracts;

/// <summary>
/// Discount-code generation, for sections that hand a human a code: Campaigns' grant
/// waves today, and a low-income concession next.
/// </summary>
/// <remarks>
/// Tickets is the application's only door to ticketing, so a section asks <em>Tickets</em>
/// for codes and Tickets asks the vendor. Campaigns used to call
/// <c>ITicketVendorService.GenerateDiscountCodesAsync</c> directly; that cow path is closed
/// here, which is what makes the second consumer a project reference rather than a design
/// conversation. Nothing outside Humans.Tickets and Shell's vendor health check may inject
/// the vendor port — pinned by <c>TicketVendorPortArchitectureTests</c>.
/// </remarks>
public interface ITicketDiscountCodes : IApplicationService
{
    /// <summary>
    /// Generates discount codes at the ticket vendor and returns them. The codes are
    /// live the moment they come back; the caller owns delivering them.
    /// </summary>
    [ExternalWrite]
    Task<IReadOnlyList<string>> GenerateAsync(
        TicketDiscountCodeRequest request, CancellationToken ct = default);
}

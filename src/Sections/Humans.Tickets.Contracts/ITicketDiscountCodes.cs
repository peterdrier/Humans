using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.Tickets.Contracts;

/// <summary>
/// Discount codes for sections that hand a member a code. Sections ask Tickets, never the vendor port, which
/// only Humans.Tickets may inject.
/// </summary>
public interface ITicketDiscountCodes : IApplicationService
{
    /// <summary>The codes are live when they return; the caller owns delivering them.</summary>
    [ExternalWrite]
    Task<IReadOnlyList<string>> GenerateAsync(
        TicketDiscountCodeRequest request, CancellationToken ct = default);
}

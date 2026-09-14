using Humans.Base.Attributes;

namespace Humans.Tickets.Contracts;

/// <summary>
/// Cross-section read surface for Tickets. External sections inject this
/// interface; it exposes only DTO/read-model projections, primitives, NodaTime,
/// and collections. It must not expose EF entity types.
/// </summary>
/// <remarks>
/// Every call site outside the section uses only these
/// methods. A caller wanting a derived aggregate gets a property on
/// <see cref="TicketOrderInfo"/>, not a third method
/// (memory/architecture/read-model-enrichment.md); <see cref="SurfaceBudgetAttribute"/>
/// is what makes that the cheaper path, and raising it is the owner's call, out of band.
/// </remarks>
public interface ITicketServiceRead
{
    /// <summary>
    /// Returns the ticket order projection used by cross-section read callers.
    /// Callers derive aggregate questions from this DTO instead of adding
    /// one-off service methods.
    /// </summary>
    Task<IReadOnlyList<TicketOrderInfo>> GetTicketOrdersAsync(CancellationToken ct = default);

    /// <summary>
    /// Snapshot of a user's ticket holdings: count of orders where they're the
    /// buyer, plus the attendee names of every ticket where they are the
    /// current owner.
    /// </summary>
    Task<UserTicketHoldings> GetUserTicketHoldingsAsync(Guid userId, CancellationToken ct = default);
}

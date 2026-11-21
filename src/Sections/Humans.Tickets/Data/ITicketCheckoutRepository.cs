using NodaTime;
using Humans.Base.Interfaces.Repositories;
using Humans.Tickets.Domain.Checkout;

namespace Humans.Tickets.Data;

/// <summary>The checkout lane's tables: sales events, checkout orders and their refunds.</summary>
internal interface ITicketCheckoutRepository : IRepository
{
    Task<IReadOnlyList<TicketSalesEvent>> GetSalesEventsAsync(CancellationToken ct = default);

    Task<TicketSalesEvent?> GetSalesEventAsync(Guid id, CancellationToken ct = default);

    Task<TicketSalesEvent?> GetSalesEventForEventAsync(Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>Inserts or replaces the whole row, owned lists included.</summary>
    Task SaveSalesEventAsync(TicketSalesEvent salesEvent, CancellationToken ct = default);

    Task<TicketCheckoutOrder?> GetOrderAsync(Guid id, CancellationToken ct = default);

    Task<TicketCheckoutOrder?> GetOrderByBackendIdAsync(string backendKey, string backendOrderId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForSalesEventAsync(Guid salesEventId, CancellationToken ct = default);

    /// <summary>Orders the person paid for or holds a ticket line in.</summary>
    Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Created, Pending or Processing orders on any event, for the reconcile poll.</summary>
    Task<IReadOnlyList<TicketCheckoutOrder>> GetOpenOrdersAsync(CancellationToken ct = default);

    Task AddOrderAsync(TicketCheckoutOrder order, CancellationToken ct = default);

    Task UpdateOrderAsync(TicketCheckoutOrder order, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsAsync(Guid orderId, CancellationToken ct = default);

    Task AddRefundAsync(TicketCheckoutRefund refund, CancellationToken ct = default);

    Task UpdateRefundAsync(TicketCheckoutRefund refund, CancellationToken ct = default);

    /// <summary>The timeline is append-only: there is no update or delete.</summary>
    Task AddOrderEventAsync(TicketCheckoutOrderEvent orderEvent, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutOrderEvent>> GetOrderEventsAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>False when the order's tickets were already issued: another caller got there first.</summary>
    Task<bool> AddTicketsAsync(IReadOnlyList<TicketCheckoutTicket> tickets, CancellationToken ct = default);

    /// <summary>
    /// Moves the order to Paid only if it is not Paid yet, in one conditional update. False when another caller
    /// already did: only the caller that gets true fulfils the order.
    /// </summary>
    Task<bool> TryClaimPaidAsync(Guid orderId, Instant paidAt, CancellationToken ct = default);

    Task UpdateTicketAsync(TicketCheckoutTicket ticket, CancellationToken ct = default);

    Task<TicketCheckoutTicket?> GetTicketAsync(Guid id, CancellationToken ct = default);

    Task<TicketCheckoutTicket?> GetTicketByBarcodeAsync(string barcode, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForOrderAsync(Guid orderId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForHolderAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForSalesEventAsync(Guid salesEventId, CancellationToken ct = default);

    Task AddOfferAsync(TicketResaleOffer offer, CancellationToken ct = default);

    Task UpdateOfferAsync(TicketResaleOffer offer, CancellationToken ct = default);

    Task<TicketResaleOffer?> GetOfferAsync(Guid id, CancellationToken ct = default);

    Task<TicketResaleOffer?> GetOfferByTokenHashAsync(string tokenHash, CancellationToken ct = default);

    Task<IReadOnlyList<TicketResaleOffer>> GetOffersForSalesEventAsync(Guid salesEventId, CancellationToken ct = default);

    /// <summary>Every refund on the sales event's orders.</summary>
    Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsForSalesEventAsync(Guid salesEventId, CancellationToken ct = default);

    /// <summary>
    /// Tombstones the person's names and emails, their children's names on their orders and tickets, partner lines and
    /// tickets without an account that carry one of <paramref name="emails"/>, and invitations sent to any of them.
    /// Amounts stay for the books.
    /// </summary>
    Task<int> ErasePiiForUserAsync(Guid userId, IReadOnlyCollection<string> emails, CancellationToken ct = default);

    Task ReassignAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default);
}

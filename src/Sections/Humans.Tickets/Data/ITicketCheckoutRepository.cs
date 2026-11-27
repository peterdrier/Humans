using NodaTime;
using Humans.Base.Interfaces.Repositories;
using Humans.Tickets.Domain.Checkout;

namespace Humans.Tickets.Data;

/// <summary>The checkout lane's tables: ticketing settings, orders and their refunds.</summary>
internal interface ITicketCheckoutRepository : IRepository
{
    Task<IReadOnlyList<TicketingSettings>> ListTicketingSettingsAsync(CancellationToken ct = default);

    Task<TicketingSettings?> GetTicketingSettingsAsync(Guid id, CancellationToken ct = default);

    Task<TicketingSettings?> GetTicketingSettingsForEventAsync(Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>Inserts or replaces the whole row, owned lists included.</summary>
    Task SaveTicketingSettingsAsync(TicketingSettings ticketing, CancellationToken ct = default);

    Task<TicketCheckoutOrder?> GetOrderAsync(Guid id, CancellationToken ct = default);

    Task<TicketCheckoutOrder?> GetOrderByPlatformIdAsync(string platformKey, string platformOrderId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForSettingsAsync(Guid settingsId, CancellationToken ct = default);

    /// <summary>Orders the person paid for or holds a ticket line in.</summary>
    Task<IReadOnlyList<TicketCheckoutOrder>> GetOrdersForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Created, Pending or Processing orders on any event, for the reconcile poll.</summary>
    Task<IReadOnlyList<TicketCheckoutOrder>> GetOpenOrdersAsync(CancellationToken ct = default);

    /// <summary>Saves the order and any initial ticket in one commit.</summary>
    Task AddOrderAsync(TicketCheckoutOrder order, CancellationToken ct = default, TicketCheckoutTicket? ticket = null);

    /// <summary>Saves the order and optional renamed ticket in one commit.</summary>
    Task UpdateOrderAsync(TicketCheckoutOrder order, CancellationToken ct = default, TicketCheckoutTicket? ticket = null);

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

    /// <summary>Saves the ticket, optional dependents and resale offer in one commit.</summary>
    Task UpdateTicketAsync(TicketCheckoutTicket ticket, CancellationToken ct = default,
        IReadOnlyList<TicketCheckoutTicket>? dependents = null, TicketResaleOffer? offer = null);

    Task<TicketCheckoutTicket?> GetTicketAsync(Guid id, CancellationToken ct = default);

    Task<TicketCheckoutTicket?> GetTicketByBarcodeAsync(string barcode, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForOrderAsync(Guid orderId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForHolderAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForSettingsAsync(Guid settingsId, CancellationToken ct = default);

    Task AddOfferAsync(TicketResaleOffer offer, CancellationToken ct = default);

    Task UpdateOfferAsync(TicketResaleOffer offer, CancellationToken ct = default);

    Task<TicketResaleOffer?> GetOfferAsync(Guid id, CancellationToken ct = default);

    Task<TicketResaleOffer?> GetOfferByTokenHashAsync(string tokenHash, CancellationToken ct = default);

    Task<IReadOnlyList<TicketResaleOffer>> GetOffersForSettingsAsync(Guid settingsId, CancellationToken ct = default);

    Task AddCompanionRequestAsync(TicketCompanionRequest request, CancellationToken ct = default);

    Task UpdateCompanionRequestAsync(TicketCompanionRequest request, CancellationToken ct = default);

    Task<TicketCompanionRequest?> GetCompanionRequestAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCompanionRequest>> GetCompanionRequestsForSettingsAsync(Guid settingsId, CancellationToken ct = default);

    /// <summary>Requests where the person is the holder or the companion.</summary>
    Task<IReadOnlyList<TicketCompanionRequest>> GetCompanionRequestsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Every refund on the event's orders.</summary>
    Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsForSettingsAsync(Guid settingsId, CancellationToken ct = default);

    /// <summary>
    /// Tombstones the person's names and emails, their children's names on their orders and tickets, lines and
    /// tickets without an account that carry one of <paramref name="emails"/>, invitations sent to any of them and the
    /// notes on companion requests they are part of. Amounts stay for the books.
    /// </summary>
    Task<int> ErasePiiForUserAsync(Guid userId, IReadOnlyCollection<string> emails, CancellationToken ct = default);

    Task ReassignAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default);
}

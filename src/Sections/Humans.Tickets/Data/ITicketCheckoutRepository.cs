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

    /// <summary>Created or Pending orders on any event, for the reconcile poll.</summary>
    Task<IReadOnlyList<TicketCheckoutOrder>> GetOpenOrdersAsync(CancellationToken ct = default);

    Task AddOrderAsync(TicketCheckoutOrder order, CancellationToken ct = default);

    Task UpdateOrderAsync(TicketCheckoutOrder order, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>Every refund on the event's orders, for the ticketing team's refund list.</summary>
    Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsForSalesEventAsync(Guid salesEventId, CancellationToken ct = default);

    Task AddRefundAsync(TicketCheckoutRefund refund, CancellationToken ct = default);

    Task UpdateRefundAsync(TicketCheckoutRefund refund, CancellationToken ct = default);

    /// <summary>The timeline is append-only: there is no update or delete.</summary>
    Task AddOrderEventAsync(TicketCheckoutOrderEvent orderEvent, CancellationToken ct = default);

    Task<IReadOnlyList<TicketCheckoutOrderEvent>> GetOrderEventsAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>Replaces names and emails on the person's orders and ticket lines; amounts stay for the books.</summary>
    Task<int> ErasePiiForUserAsync(Guid userId, CancellationToken ct = default);

    Task ReassignAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default);
}

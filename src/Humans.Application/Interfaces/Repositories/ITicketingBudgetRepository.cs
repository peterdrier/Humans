using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Narrow repository owned by <see cref="Humans.Application.Services.Tickets.TicketingBudgetService"/>.
/// Exposes only the ticket-sales shape that the Tickets→Budget bridge needs —
/// paid orders with pre-counted valid/checked-in attendees — so the service can
/// bucket them into ISO weeks in memory without taking a direct dependency on
/// <c>DbContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the (pending) broader <c>ITicketRepository</c> that
/// <c>TicketQueryService</c> and <c>TicketSyncService</c> will adopt in the
/// follow-up sub-tasks (#545a and #545c). Keeping this interface narrow avoids
/// stepping on that larger surface while TicketingBudgetService migrates on its
/// own timeline as the Tickets→Budget bridge.
/// </para>
/// <para>
/// Table ownership per design-rules §8: <c>ticket_orders</c>/<c>ticket_attendees</c>
/// are Tickets-section-owned. This repository is a Tickets-owned read gateway
/// used by the in-section bridge service (TicketingBudgetService co-owns Tickets
/// tables). <c>ticketing_projections</c> remains Budget-owned and flows through
/// <c>IBudgetService</c> — unchanged by this migration.
/// </para>
/// </remarks>
public interface ITicketingBudgetRepository
{
    /// <summary>
    /// Returns one summary row per ticket order whose
    /// <c>PaymentStatus == TicketPaymentStatus.Paid</c>, with <c>TicketCount</c>
    /// pre-computed server-side as the number of attendees with
    /// <c>Status</c> equal to <c>Valid</c> or <c>CheckedIn</c>. Read-only
    /// (AsNoTracking). Ordering is unspecified — the caller groups by ISO week.
    /// </summary>
    Task<IReadOnlyList<PaidTicketOrderSummary>> GetPaidOrderSummariesAsync(
        CancellationToken ct = default);
}

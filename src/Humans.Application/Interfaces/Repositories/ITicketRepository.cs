using Humans.Application.DTOs;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using Humans.Domain.Attributes;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Tickets section's canonical tables
/// (<c>ticket_orders</c>, <c>ticket_attendees</c>, <c>ticket_sync_states</c>).
/// Owned by <see cref="Humans.Application.Services.Tickets.TicketSyncService"/>
/// — the only non-test code path that writes to these tables.
/// </summary>
/// <remarks>
/// <para>
/// Narrow shape: exposes only the operations the Application-layer
/// <c>TicketSyncService</c> needs. Business rules (VAT computation, OAuth
/// tie-breaking, status parsing) stay in the service; this interface just
/// loads and persists. Every method opens its own short-lived
/// <c>HumansDbContext</c> via <c>IDbContextFactory</c> and disposes it before
/// returning — returned entities are therefore detached (<c>AsNoTracking</c>);
/// callers mutate them in memory and pass them back to the matching write
/// method.
/// </para>
/// </remarks>
[Section("Tickets")]
public interface ITicketRepository : IRepository
{
    // ── TicketSyncState (singleton row, Id == 1) ─────────────────────────────

    /// <summary>
    /// Returns the sync-state singleton row (Id == 1), read-only.
    /// Returns null if the seed row is missing — callers should treat this as
    /// a configuration error.
    /// </summary>
    Task<TicketSyncState?> GetSyncStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists the in-memory state of the sync-state singleton row.
    /// Attaches the entity and marks it modified. If the row does not yet
    /// exist, it is inserted instead.
    /// </summary>
    Task PersistSyncStateAsync(TicketSyncState state, CancellationToken ct = default);

    /// <summary>
    /// Clears <c>LastSyncAt</c> on the singleton row to force a full resync
    /// on the next sync cycle. No-op if the row does not exist.
    /// </summary>
    Task ResetSyncStateLastSyncAsync(CancellationToken ct = default);

    // ── TicketOrder reads (all detached / AsNoTracking) ──────────────────────

    /// <summary>
    /// Returns detached <see cref="TicketOrder"/> entities keyed by
    /// <c>VendorOrderId</c> for the given set of vendor ids. Callers mutate
    /// these in memory and pass them back to <see cref="UpsertOrdersAsync"/>.
    /// </summary>
    Task<IReadOnlyDictionary<string, TicketOrder>> GetOrdersByVendorIdsAsync(
        IReadOnlyCollection<string> vendorOrderIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns detached <see cref="TicketAttendee"/> entities keyed by
    /// <c>VendorTicketId</c> for the given set of vendor ticket ids. Callers
    /// mutate these in memory and pass them back to
    /// <see cref="UpsertAttendeesAsync"/>.
    /// </summary>
    Task<IReadOnlyDictionary<string, TicketAttendee>> GetAttendeesByVendorIdsAsync(
        IReadOnlyCollection<string> vendorTicketIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns <c>(VendorOrderId, Id)</c> pairs for every order in the database
    /// — used by the attendee-upsert pass to resolve parent-order FKs without
    /// re-querying per ticket.
    /// </summary>
    Task<IReadOnlyDictionary<string, Guid>> GetOrderIdsByVendorIdAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Returns all orders with <c>StripePaymentIntentId != null</c> and
    /// <c>StripeFee == null</c>, detached. Callers fill in fee/payment-method
    /// data and save via <see cref="UpsertOrdersAsync"/>.
    /// </summary>
    Task<IReadOnlyList<TicketOrder>> GetOrdersNeedingStripeEnrichmentAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Returns all orders with their attendees eagerly loaded (same aggregate
    /// — not a cross-section include). Detached. Used by the VAT-computation
    /// pass which writes <c>VatAmount</c> back on each order.
    /// </summary>
    Task<IReadOnlyList<TicketOrder>> GetAllOrdersWithAttendeesAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Returns discount-code rows for every order with a non-null
    /// <c>DiscountCode</c>. Read-only.
    /// </summary>
    Task<IReadOnlyList<OrderDiscountCodeRow>> GetOrderDiscountCodesAsync(
        CancellationToken ct = default);

    // ── TicketAttendee reads ─────────────────────────────────────────────────

    /// <summary>
    /// Returns a single <see cref="TicketAttendee"/> with its parent
    /// <see cref="TicketOrder"/> eagerly loaded (required by callers that need
    /// <c>TicketOrder.MatchedUserId</c>). Returns <c>null</c> when not found.
    /// Used by the transfer-request flow so it can validate ownership without
    /// loading the full attendee set.
    /// </summary>
    /// <remarks>
    /// Does NOT include sibling <see cref="TicketOrder.Attendees"/> — that path
    /// (TicketAttendee → TicketOrder → Attendees) is a cycle and EF Core
    /// rejects it under <c>AsNoTracking</c>. Callers that need sibling vendor
    /// ticket ids should call <see cref="GetVendorTicketIdsForOrderAsync"/>.
    /// </remarks>
    Task<TicketAttendee?> GetAttendeeByIdAsync(Guid attendeeId, CancellationToken ct = default);

    /// <summary>
    /// Returns the <c>VendorTicketId</c>s of every attendee on a given order,
    /// in stable order by vendor ticket id. Used by the transfer admin detail
    /// page to render sibling tickets without loading the full
    /// <see cref="TicketAttendee"/> graph (which would form a cycle through
    /// <see cref="TicketOrder.Attendees"/>).
    /// </summary>
    Task<IReadOnlyList<string>> GetVendorTicketIdsForOrderAsync(
        Guid orderId, CancellationToken ct = default);

    /// <summary>
    /// Get all TicketAttendees the user is attached to: either as the buyer
    /// (TicketOrder.MatchedUserId == userId) or as the matched recipient
    /// (TicketAttendee.MatchedUserId == userId). Used by the homepage ticket
    /// card to enumerate visible attendees, including ones transferred in.
    /// </summary>
    Task<IReadOnlyList<TicketAttendee>> GetAttendeesVisibleToUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns matched-attendee rows (MatchedUserId non-null) for a single
    /// vendor event. Read-only projection used by the event-participation
    /// reconciliation pass.
    /// </summary>
    Task<IReadOnlyList<MatchedAttendeeRow>> GetMatchedAttendeesForEventAsync(
        string vendorEventId,
        CancellationToken ct = default);

    // ── TicketOrder / TicketAttendee writes ──────────────────────────────────

    /// <summary>
    /// Upserts the given orders — existing rows (matched by
    /// <c>VendorOrderId</c>) are updated, the rest are inserted — in a single
    /// <c>SaveChanges</c>.
    /// </summary>
    Task UpsertOrdersAsync(IReadOnlyList<TicketOrder> orders, CancellationToken ct = default);

    /// <summary>
    /// Upserts the given attendees — existing rows (matched by
    /// <c>VendorTicketId</c>) are updated, the rest are inserted — in a single
    /// <c>SaveChanges</c>.
    /// </summary>
    Task UpsertAttendeesAsync(IReadOnlyList<TicketAttendee> attendees, CancellationToken ct = default);

    /// <summary>
    /// Returns every <see cref="TicketAttendee"/> for the given vendor event
    /// that is currently unmatched (<c>MatchedUserId is null</c>) and whose
    /// <see cref="Humans.Domain.Enums.TicketAttendeeStatus"/> is
    /// <c>Valid</c> or <c>CheckedIn</c>, AND whose
    /// <see cref="TicketAttendee.AttendeeEmail"/> is non-empty.
    ///
    /// Used by the attendee contact import to identify candidates for user
    /// provisioning. Returns the full entity so the caller can mutate
    /// <c>MatchedUserId</c> and pass the list back to
    /// <see cref="UpsertAttendeesAsync"/>.
    /// </summary>
    Task<IReadOnlyList<TicketAttendee>> GetUnmatchedActiveAttendeesAsync(
        string vendorEventId, CancellationToken ct = default);

    /// <summary>
    /// Persists <c>VatAmount</c> updates for the given orders in a single
    /// <c>SaveChanges</c>. Only the VAT column is written — other mutations
    /// on the entities are ignored.
    /// </summary>
    Task UpdateOrderVatAmountsAsync(
        IReadOnlyList<(Guid OrderId, decimal VatAmount)> updates,
        CancellationToken ct = default);

    /// <summary>
    /// Persists Stripe-enrichment updates (<c>PaymentMethod</c>,
    /// <c>PaymentMethodDetail</c>, <c>StripeFee</c>, <c>ApplicationFee</c>) for
    /// the given orders in a single <c>SaveChanges</c>.
    /// </summary>
    Task UpdateOrderStripeEnrichmentAsync(
        IReadOnlyList<TicketOrder> orders,
        CancellationToken ct = default);

    // ── TicketQueryService reads (matching / dashboard / exports) ────────────

    Task<int> CountValidAttendeesMatchedToUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns all non-null <c>AttendeeEmail</c> values from attendees in
    /// <c>Valid</c> or <c>CheckedIn</c> state. Callers filter case-insensitively
    /// in memory (typical caller caches the result on a short TTL).
    /// </summary>
    Task<IReadOnlyList<string>> GetValidAttendeeEmailsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns distinct <c>MatchedUserId</c> values for attendees in
    /// <c>Valid</c> or <c>CheckedIn</c> state whose owning order's
    /// <c>VendorEventId</c> equals <paramref name="vendorEventId"/>. Used by
    /// audience-side ticket-holder enumeration and the dashboard volunteer
    /// coverage stat — both want "current event" semantics.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetValidMatchedAttendeeUserIdsForEventAsync(
        string vendorEventId, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetAllMatchedAttendeeUserIdsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetAllMatchedOrderUserIdsAsync(CancellationToken ct = default);

    Task<bool> HasEventTicketAsync(Guid userId, string vendorEventId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetDistinctTicketTypesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<TicketOrder>> GetOrdersMatchedToUserAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketAttendee>> GetAttendeesMatchedToUserAsync(Guid userId, CancellationToken ct = default);

    Task<TicketDashboardTotals> GetDashboardTotalsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PaidOrderPaymentMethodRow>> GetPaidOrderPaymentMethodsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<OrderDateAndCount>> GetOrderDateAttendeeCountsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<RecentOrder>> GetRecentOrdersAsync(int count, CancellationToken ct = default);

    Task<IReadOnlyList<PaidOrderSalesRow>> GetPaidOrderSalesRowsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DiscountCodeOrderRow>> GetOrdersWithDiscountCodesAsync(CancellationToken ct = default);

    Task<(IReadOnlyList<OrderRow> Rows, int TotalCount)> GetOrdersPageAsync(
        string? search,
        string sortBy,
        bool sortDesc,
        int page,
        int pageSize,
        TicketPaymentStatus? filterPaymentStatus,
        string? filterTicketType,
        bool? filterMatched,
        CancellationToken ct = default);

    Task<(IReadOnlyList<AttendeeRow> Rows, int TotalCount)> GetAttendeesPageAsync(
        string? search,
        string sortBy,
        bool sortDesc,
        int page,
        int pageSize,
        string? filterTicketType,
        TicketAttendeeStatus? filterStatus,
        bool? filterMatched,
        string? filterOrderId,
        bool filterMultipleTickets,
        CancellationToken ct = default);

    Task<IReadOnlyList<AttendeeExportRow>> GetAttendeeExportDataAsync(CancellationToken ct = default);

    Task<IReadOnlyList<OrderExportRow>> GetOrderExportDataAsync(CancellationToken ct = default);

    Task<TicketSyncState?> ResetStaleRunningStateAsync(
        Instant olderThan,
        Instant now,
        string errorMessage,
        CancellationToken ct = default);

    // ── Admin diagnostics ────────────────────────────────────────────────────

    /// <summary>
    /// Returns paid orders whose live-ticket count (Valid or CheckedIn) is
    /// less than their total attendee count. Used by the admin order-drift
    /// diagnostic.
    /// </summary>
    Task<IReadOnlyList<OrderDriftRow>> GetOrderDriftAsync(CancellationToken ct = default);

    // ── Account-merge fold ───────────────────────────────────────────────────

    /// <summary>
    /// Bulk-moves Tickets-section rows from <paramref name="sourceUserId"/>
    /// to <paramref name="targetUserId"/> for the account-merge fold flow.
    /// Re-FKs <c>ticket_orders.MatchedUserId</c> and
    /// <c>ticket_attendees.MatchedUserId</c> in a single
    /// <c>SaveChanges</c>. Tickets are unique per purchase — no dedup is
    /// needed; this is plain re-FK. The <paramref name="updatedAt"/>
    /// parameter is accepted for signature parity with other
    /// <c>Reassign…ToUserAsync</c> methods across the merge fold but is
    /// <b>unused</b> — neither <c>TicketOrder</c> nor <c>TicketAttendee</c>
    /// carries a generic <c>UpdatedAt</c> column (only <c>SyncedAt</c>,
    /// owned by the vendor-sync pipeline). Returns the count of
    /// <c>ticket_attendees</c> rows ultimately attributed to
    /// <paramref name="targetUserId"/>.
    /// </summary>
    Task<int> ReassignToUserAsync(Guid sourceUserId, Guid targetUserId, Instant updatedAt, CancellationToken ct = default);
}

using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

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
/// <para>
/// <b>Relation to <c>ITicketingBudgetRepository</c>:</b> Intentionally
/// distinct. That repository is a narrow read-only shape for the
/// Tickets→Budget bridge (<c>TicketingBudgetService</c>). Merging the two
/// would couple TicketSync's write surface to Budget's read surface — two
/// things that evolve on very different cadences.
/// </para>
/// </remarks>
public interface ITicketRepository
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

    // ── Email lookup (cross-section projection over user_emails) ─────────────

    /// <summary>
    /// Returns every row of <c>user_emails</c> projected to the three fields
    /// needed to build the email → userId lookup. Read-only; ordering is
    /// unspecified — the service performs grouping and OAuth tie-breaking in
    /// memory.
    /// </summary>
    /// <remarks>
    /// This projection is narrow (three columns) and non-mutating, so reading
    /// it from the Tickets repository does not violate table ownership — the
    /// Profile section's owning service (<c>IUserEmailService</c>) does not
    /// expose a shape that fits bulk email-matching. If TicketSync ever needs
    /// to <i>write</i> user-email data, it must go through
    /// <c>IUserEmailService</c> instead.
    /// </remarks>
    Task<IReadOnlyList<UserEmailLookupEntry>> GetAllUserEmailLookupEntriesAsync(
        CancellationToken ct = default);

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

    Task<int> CountValidAttendeesByUppercaseEmailsAsync(
        IReadOnlyCollection<string> uppercaseEmails, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetValidMatchedAttendeeUserIdsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetAllMatchedAttendeeUserIdsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetAllMatchedOrderUserIdsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetMatchedUserIdsForPaidOrdersAsync(CancellationToken ct = default);

    Task<bool> HasAnyTicketMatchAsync(Guid userId, CancellationToken ct = default);

    Task<bool> HasEventTicketAsync(Guid userId, string vendorEventId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetDistinctTicketTypesAsync(CancellationToken ct = default);

    Task<int> CountSoldAttendeesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<TicketOrder>> GetOrdersMatchedToUserAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketAttendee>> GetAttendeesMatchedToUserAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<Instant>> GetPaidOrderDatesInWindowAsync(
        Instant fromInclusive,
        Instant toExclusive,
        CancellationToken ct = default);

    Task<TicketDashboardTotals> GetDashboardTotalsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PaidOrderPaymentMethodRow>> GetPaidOrderPaymentMethodsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<OrderDateAndCount>> GetOrderDateAttendeeCountsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<RecentOrder>> GetRecentOrdersAsync(int count, CancellationToken ct = default);

    Task<decimal> GetGrossPaidRevenueAsync(CancellationToken ct = default);

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
        CancellationToken ct = default);

    Task<IReadOnlyList<AttendeeExportRow>> GetAttendeeExportDataAsync(CancellationToken ct = default);

    Task<IReadOnlyList<OrderExportRow>> GetOrderExportDataAsync(CancellationToken ct = default);

    Task<TicketSyncState?> ResetStaleRunningStateAsync(
        Instant olderThan,
        Instant now,
        string errorMessage,
        CancellationToken ct = default);
}

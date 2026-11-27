using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

/// <summary>
/// The checkout backends seen from the ticketing team's desk: which are registered, a read-only check, each one's
/// ledger of orders and refunds, and reconciliation of what the backend holds against Humans' orders. Reads only.
/// </summary>
internal interface ITicketReconService : IApplicationService
{
    IReadOnlyList<CheckoutBackendInfo> Backends { get; }

    /// <summary>Runs the backend's read-only check, for an event when its id in the backend is given; null for an unknown key.</summary>
    Task<IReadOnlyList<CheckoutBackendFinding>?> CheckBackendAsync(string key, string? backendEventRef, CancellationToken ct = default);

    /// <summary>
    /// Financial reconciliation of one backend for [<paramref name="from"/>, <paramref name="to"/>): Humans' orders
    /// against the backend's own list. Null for an unknown backend; a backend that cannot be read gives a result
    /// carrying the error, never a partial comparison.
    /// </summary>
    Task<TicketRecon?> ReconcileBackendAsync(string backendKey, Instant from, Instant to, CancellationToken ct = default);

    /// <summary>
    /// Fee and payout reconciliation of one backend for [<paramref name="from"/>, <paramref name="to"/>): the payment
    /// account's own movements against Humans' orders. Null for an unknown or unconfigured backend and for one whose
    /// payment account Humans cannot read; an account that cannot be read gives a result carrying the error.
    /// </summary>
    Task<TicketPaymentRecon?> ReconcilePaymentsAsync(string backendKey, Instant from, Instant to, CancellationToken ct = default);

    /// <summary>Every order opened on one backend, newest first, with its stuck orders and unsettled refunds; null for an unknown key.</summary>
    Task<BackendLedger?> GetBackendLedgerAsync(string backendKey, CancellationToken ct = default);

    /// <summary>The backends some sales setup sells through, for the reconciliation page and its daily run.</summary>
    Task<IReadOnlyList<string>> GetBackendsInUseAsync(CancellationToken ct = default);
}

internal sealed record CheckoutBackendInfo(string Key, bool IsConfigured, CheckoutBackendSetup Setup);

/// <summary>
/// <paramref name="Paid"/>: paid orders as reconciliation counts them, free orders left out. <paramref name="Stuck"/>:
/// orders still open well past their expiry, which neither a webhook nor the five-minute reconcile has settled.
/// <paramref name="UnsettledRefunds"/>: pending or waiting on a person. <paramref name="FailedRefunds"/>: failed, with
/// no later attempt on the order that went through.
/// </summary>
internal sealed record BackendLedger(
    CheckoutBackendInfo Backend,
    IReadOnlyList<TicketCheckoutOrder> Orders,
    IReadOnlyList<TicketCheckoutOrder> Paid,
    IReadOnlyList<TicketCheckoutOrder> Stuck,
    IReadOnlyList<TicketCheckoutRefund> UnsettledRefunds,
    IReadOnlyList<TicketCheckoutRefund> FailedRefunds);

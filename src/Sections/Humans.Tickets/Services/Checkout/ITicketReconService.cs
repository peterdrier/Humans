using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

/// <summary>
/// The ticketing platforms seen from the ticketing team's desk: which are registered, a read-only check, each one's
/// ledger of orders and refunds, and reconciliation of what the platform holds against Humans' orders. Reads only.
/// </summary>
internal interface ITicketReconService : IApplicationService
{
    IReadOnlyList<PlatformInfo> Platforms { get; }

    /// <summary>Runs the platform's read-only check, for an event when its id in the platform is given; null for an unknown key.</summary>
    Task<IReadOnlyList<PlatformFinding>?> CheckPlatformAsync(string key, string? platformEventRef, CancellationToken ct = default);

    /// <summary>
    /// Financial reconciliation of one platform for [<paramref name="from"/>, <paramref name="to"/>): Humans' orders
    /// against the platform's own list. Null for an unknown platform; a platform that cannot be read gives a
    /// result carrying the error, never a partial comparison.
    /// </summary>
    Task<TicketRecon?> ReconcilePlatformAsync(string platformKey, Instant from, Instant to, CancellationToken ct = default);

    /// <summary>
    /// Fee and payout reconciliation of one platform for [<paramref name="from"/>, <paramref name="to"/>): the payment
    /// account's own movements against Humans' orders. Null for an unknown or unconfigured platform and for one
    /// whose payment account Humans cannot read; an account that cannot be read gives a result carrying the error.
    /// </summary>
    Task<TicketPaymentRecon?> ReconcilePaymentsAsync(string platformKey, Instant from, Instant to, CancellationToken ct = default);

    /// <summary>Every order opened on one platform, newest first, with its stuck orders and unsettled refunds; null for an unknown key.</summary>
    Task<PlatformLedger?> GetPlatformLedgerAsync(string platformKey, CancellationToken ct = default);

    /// <summary>The platforms some event's ticketing settings sell through, for the reconciliation page and its daily run.</summary>
    Task<IReadOnlyList<string>> GetPlatformsInUseAsync(CancellationToken ct = default);
}

internal sealed record PlatformInfo(string Key, bool IsConfigured, PlatformSetup Setup);

/// <summary>
/// <paramref name="Paid"/>: paid orders as reconciliation counts them, free orders left out. <paramref name="Stuck"/>:
/// orders still open well past their expiry, which neither a webhook nor the five-minute reconcile has settled.
/// <paramref name="UnsettledRefunds"/>: pending or waiting on a person. <paramref name="FailedRefunds"/>: failed, with
/// no later attempt on the order that went through.
/// </summary>
internal sealed record PlatformLedger(
    PlatformInfo Platform,
    IReadOnlyList<TicketCheckoutOrder> Orders,
    IReadOnlyList<TicketCheckoutOrder> Paid,
    IReadOnlyList<TicketCheckoutOrder> Stuck,
    IReadOnlyList<TicketCheckoutRefund> UnsettledRefunds,
    IReadOnlyList<TicketCheckoutRefund> FailedRefunds);

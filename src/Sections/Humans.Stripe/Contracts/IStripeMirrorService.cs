using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Stripe.Contracts;

/// <summary>Reads of the local Stripe mirror. Never calls Stripe: figures are as of each account's last sweep.</summary>
public interface IStripeMirrorService : IApplicationService
{
    /// <summary>Each mirrored account's EUR balance as Stripe last reported it. An account with no mirrored EUR
    /// balance (never swept, or holding only other currencies) is absent, never a zero.</summary>
    Task<IReadOnlyList<StripeAccountBalance>> GetEurBalancesAsync(CancellationToken ct = default);
}

/// <summary><paramref name="AccountId"/> is Stripe's <c>acct_…</c> id. <paramref name="SyncedAt"/> is when the sweep
/// that read it ran.</summary>
public sealed record StripeAccountBalance(
    string AccountId, decimal AvailableEur, decimal PendingEur, Instant SyncedAt)
{
    public decimal TotalEur => AvailableEur + PendingEur;
}

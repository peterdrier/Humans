using Humans.Base.Interfaces.Repositories;
using Humans.Stripe.Domain;
using NodaTime;

namespace Humans.Stripe.Data;

internal interface IStripeMirrorRepository : IRepository
{
    /// <summary>The sweep is the truth for its account: that account's transactions, payouts and balances it did not
    /// return are deleted. One save, so a page never sees half a sweep. Other accounts are not touched.</summary>
    Task ReplaceAccountAsync(StripeAccountSnapshot snapshot, Instant now, CancellationToken ct = default);

    /// <summary>Every transaction, without <see cref="StripeBalanceTransaction.Raw"/>.</summary>
    Task<IReadOnlyList<StripeBalanceTransaction>> GetTransactionsAsync(CancellationToken ct = default);

    Task<StripeBalanceTransaction?> GetTransactionAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<StripePayout>> GetPayoutsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<StripeBalance>> GetBalancesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<StripeSyncState>> GetSyncStatesAsync(CancellationToken ct = default);
    Task SaveSyncStateAsync(StripeSyncState state, CancellationToken ct = default);
}

internal sealed record StripeAccountSnapshot(
    string AccountId,
    IReadOnlyList<StripeBalanceTransaction> Transactions,
    IReadOnlyList<StripePayout> Payouts,
    IReadOnlyList<StripeBalance> Balances);

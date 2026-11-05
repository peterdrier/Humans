using Humans.Stripe.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Stripe.Data;

internal sealed class Repository(IDbContextFactory<StripeDbContext> factory) : IStripeMirrorRepository
{
    public async Task ReplaceAccountAsync(StripeAccountSnapshot snapshot, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var account = snapshot.AccountId;

        Replace(ctx.StripeBalanceTransactions,
            await ctx.StripeBalanceTransactions.Where(t => t.AccountId == account).ToListAsync(ct),
            snapshot.Transactions, t => t.Id, t => t.SyncedAt = now);
        Replace(ctx.StripePayouts, await ctx.StripePayouts.Where(p => p.AccountId == account).ToListAsync(ct),
            snapshot.Payouts, p => p.Id, p => p.SyncedAt = now);
        Replace(ctx.StripeBalances, await ctx.StripeBalances.Where(b => b.AccountId == account).ToListAsync(ct),
            snapshot.Balances, b => b.Currency, b => b.SyncedAt = now);

        await ctx.SaveChangesAsync(ct);
    }

    private static void Replace<T, TKey>(
        DbSet<T> set, List<T> current, IReadOnlyList<T> fetched, Func<T, TKey> key, Action<T> stamp)
        where T : class
        where TKey : notnull
    {
        var existing = current.ToDictionary(key);
        foreach (var row in fetched)
        {
            stamp(row);
            if (existing.Remove(key(row), out var cur))
                set.Entry(cur).CurrentValues.SetValues(row);
            else
                set.Add(row);
        }
        set.RemoveRange(existing.Values);
    }

    public async Task<IReadOnlyList<StripeBalanceTransaction>> GetTransactionsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.StripeBalanceTransactions.AsNoTracking()
            .Select(t => new StripeBalanceTransaction
            {
                Id = t.Id,
                AccountId = t.AccountId,
                Type = t.Type,
                ReportingCategory = t.ReportingCategory,
                Status = t.Status,
                Currency = t.Currency,
                Amount = t.Amount,
                Fee = t.Fee,
                Net = t.Net,
                Created = t.Created,
                AvailableOn = t.AvailableOn,
                Description = t.Description,
                SourceId = t.SourceId,
                PayoutId = t.PayoutId,
                PaymentIntentId = t.PaymentIntentId,
                CustomerName = t.CustomerName,
                CustomerEmail = t.CustomerEmail,
                Metadata = t.Metadata,
                SyncedAt = t.SyncedAt,
            })
            .ToListAsync(ct);
    }

    public async Task<StripeBalanceTransaction?> GetTransactionAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.StripeBalanceTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<IReadOnlyList<StripePayout>> GetPayoutsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.StripePayouts.AsNoTracking().ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StripeBalance>> GetBalancesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.StripeBalances.AsNoTracking().ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StripeSyncState>> GetSyncStatesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.StripeSyncStates.AsNoTracking().ToListAsync(ct);
    }

    public async Task SaveSyncStateAsync(StripeSyncState state, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.StripeSyncStates.FirstOrDefaultAsync(s => s.AccountId == state.AccountId, ct);
        if (existing is null)
            ctx.StripeSyncStates.Add(state);
        else
            ctx.Entry(existing).CurrentValues.SetValues(state);
        await ctx.SaveChangesAsync(ct);
    }
}

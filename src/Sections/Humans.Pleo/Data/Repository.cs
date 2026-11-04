using Humans.Pleo.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Pleo.Data;

internal sealed class Repository(IDbContextFactory<PleoDbContext> factory) : IPleoRepository
{
    public async Task ReplaceMirrorAsync(PleoMirrorSnapshot snapshot, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        Replace(ctx.PleoEntries, await ctx.PleoEntries.ToListAsync(ct), snapshot.Entries, e => e.Id, e => e.SyncedAt = now);
        Replace(ctx.PleoEmployees, await ctx.PleoEmployees.ToListAsync(ct), snapshot.Employees, e => e.Id,
            e => e.SyncedAt = now);
        Replace(ctx.PleoAccounts, await ctx.PleoAccounts.ToListAsync(ct), snapshot.Accounts, a => a.Id,
            a => a.SyncedAt = now);
        Replace(ctx.PleoBalances, await ctx.PleoBalances.ToListAsync(ct), [snapshot.Balance], b => b.Id,
            b => b.SyncedAt = now);

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

    public async Task<IReadOnlyList<PleoEntry>> GetEntriesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.PleoEntries.AsNoTracking().ToListAsync(ct);
    }

    public async Task<PleoEntry?> GetEntryAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.PleoEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<IReadOnlyList<PleoEmployee>> GetEmployeesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.PleoEmployees.AsNoTracking().ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PleoAccount>> GetAccountsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.PleoAccounts.AsNoTracking().ToListAsync(ct);
    }

    public async Task<PleoBalance?> GetBalanceAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.PleoBalances.AsNoTracking().FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<PleoCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.PleoCategories.AsNoTracking().ToListAsync(ct);
    }

    public async Task SaveCategoryAsync(PleoCategory category, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.PleoCategories.FirstOrDefaultAsync(c => c.Id == category.Id, ct);
        if (existing is null)
            ctx.PleoCategories.Add(category);
        else
            ctx.Entry(existing).CurrentValues.SetValues(category);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<PleoSyncState?> GetSyncStateAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.PleoSyncStates.AsNoTracking().FirstOrDefaultAsync(ct);
    }

    public async Task SaveSyncStateAsync(PleoSyncState state, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.PleoSyncStates.FirstOrDefaultAsync(s => s.Id == state.Id, ct);
        if (existing is null)
            ctx.PleoSyncStates.Add(state);
        else
            ctx.Entry(existing).CurrentValues.SetValues(state);
        await ctx.SaveChangesAsync(ct);
    }
}

using Humans.Holded.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Holded.Data;

internal sealed class Repository(IDbContextFactory<HoldedDbContext> factory)
    : IHoldedMirrorRepository
{
    // ── Ledger mirror ─────────────────────────────────────────────────────────

    public async Task ReplaceLedgerWindowAsync(
        Instant from, Instant to, int? accountNum,
        IReadOnlyList<HoldedLedgerLine> rows, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        // Two overlapping sets: what is currently cached in the window (candidates for deletion)
        // and whatever else carries an incoming entry number (a line Holded moved to another
        // account or another date still has to be matched by its natural key, not re-inserted).
        var entryNums = rows.Select(r => r.EntryNumber).Distinct().ToList();
        var candidates = await ctx.HoldedLedgerLines
            .Where(l => (l.Date >= from && l.Date <= to && (accountNum == null || l.AccountNum == accountNum))
                        || entryNums.Contains(l.EntryNumber))
            .ToListAsync(ct);

        // Captured before the upserts, which can move a row's date or account out of the window.
        var inWindow = candidates
            .Where(l => l.Date >= from && l.Date <= to && (accountNum is null || l.AccountNum == accountNum))
            .ToList();

        var existing = candidates.ToDictionary(l => (l.EntryNumber, l.Line));
        foreach (var r in rows)
        {
            if (existing.TryGetValue((r.EntryNumber, r.Line), out var cur))
            {
                cur.AccountNum = r.AccountNum;
                cur.Date = r.Date;
                cur.Type = r.Type;
                cur.Description = r.Description;
                cur.Debit = r.Debit;
                cur.Credit = r.Credit;
                cur.LastSyncedAt = now;
            }
            else
            {
                r.LastSyncedAt = now;
                ctx.HoldedLedgerLines.Add(r);
            }
        }

        var fetched = rows.Select(r => (r.EntryNumber, r.Line)).ToHashSet();
        ctx.HoldedLedgerLines.RemoveRange(
            inWindow.Where(l => !fetched.Contains((l.EntryNumber, l.Line))));

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedLedgerLine>> GetLedgerLinesByAccountNumAsync(
        int accountNum, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedLedgerLines.AsNoTracking()
            .Where(l => l.AccountNum == accountNum)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedLedgerLine>> GetAllLedgerLinesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedLedgerLines.AsNoTracking().ToListAsync(ct);
    }

    public async Task<bool> HasAnyLedgerLinesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedLedgerLines.AsNoTracking().AnyAsync(ct);
    }

    // ── Chart of accounts ─────────────────────────────────────────────────────

    public async Task UpsertAccountsAsync(
        IReadOnlyList<HoldedAccount> rows, Instant now, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var numbers = rows.Select(r => r.Number).ToList();
        var existing = await ctx.HoldedAccounts
            .Where(a => numbers.Contains(a.Number))
            .ToDictionaryAsync(a => a.Number, ct);
        foreach (var r in rows)
        {
            if (existing.TryGetValue(r.Number, out var cur))
            {
                cur.HoldedId = r.HoldedId;
                cur.Name = r.Name;
                cur.Group = r.Group;
                cur.Debit = r.Debit;
                cur.Credit = r.Credit;
                cur.Balance = r.Balance;
                cur.Archived = r.Archived;
                cur.SyncedAt = now;
            }
            else
            {
                r.SyncedAt = now;
                ctx.HoldedAccounts.Add(r);
            }
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedAccount>> GetAccountsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedAccounts.AsNoTracking().ToListAsync(ct);
    }

    // ── API call metering ─────────────────────────────────────────────────────

    public async Task AddApiCallsAsync(IReadOnlyList<HoldedApiCall> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.HoldedApiCalls.AddRange(rows);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedApiCall>> GetApiCallsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedApiCalls.AsNoTracking().ToListAsync(ct);
    }

    // ── Sync state ────────────────────────────────────────────────────────────

    public async Task<HoldedSyncState> GetOrCreateSyncStateAsync(
        HoldedSyncKind kind, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var state = await ctx.HoldedSyncStates.AsNoTracking().FirstOrDefaultAsync(s => s.Kind == kind, ct);
        if (state is not null) return state;

        state = new HoldedSyncState { Kind = kind };
        ctx.HoldedSyncStates.Add(state);
        await ctx.SaveChangesAsync(ct);
        return state;
    }

    public async Task SaveSyncStateAsync(HoldedSyncState state, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.HoldedSyncStates.FirstOrDefaultAsync(s => s.Kind == state.Kind, ct);
        if (existing is null)
        {
            ctx.HoldedSyncStates.Add(state);
        }
        else
        {
            ctx.Entry(existing).CurrentValues.SetValues(state);
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedSyncState>> GetSyncStatesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedSyncStates.AsNoTracking().ToListAsync(ct);
    }
}

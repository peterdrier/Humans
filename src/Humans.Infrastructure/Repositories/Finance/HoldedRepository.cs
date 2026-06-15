using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Finance;

internal sealed class HoldedRepository(IDbContextFactory<HumansDbContext> factory)
    : IHoldedRepository
{
    // ── Category map ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<HoldedCategoryMap>> GetCategoryMapAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedCategoryMap.AsNoTracking().ToListAsync(ct);
    }

    public async Task AddCategoryMapAsync(HoldedCategoryMap row, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.HoldedCategoryMap.Add(row);
        await ctx.SaveChangesAsync(ct);
    }

    // ── Docs ─────────────────────────────────────────────────────────────────

    public async Task UpsertDocsAsync(IReadOnlyList<HoldedExpenseDoc> docs, Instant now, CancellationToken ct = default)
    {
        if (docs.Count == 0) return;
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ids = docs.Select(d => d.HoldedDocId).ToList();
        var existing = await ctx.HoldedExpenseDocs
            .Where(d => ids.Contains(d.HoldedDocId)).ToDictionaryAsync(d => d.HoldedDocId, ct);
        foreach (var d in docs)
        {
            if (existing.TryGetValue(d.HoldedDocId, out var cur))
            {
                cur.DocNumber = d.DocNumber;
                cur.ContactName = d.ContactName;
                cur.Date = d.Date;
                cur.Subtotal = d.Subtotal;
                cur.Tax = d.Tax;
                cur.Total = d.Total;
                cur.Currency = d.Currency;
                cur.ApprovedAt = d.ApprovedAt;
                cur.TagsJson = d.TagsJson;
                cur.BookedAccountId = d.BookedAccountId;
                cur.BudgetCategoryId = d.BudgetCategoryId;
                cur.MatchStatus = d.MatchStatus;
                cur.MatchSource = d.MatchSource;
                cur.RawPayload = d.RawPayload;
                cur.LastSyncedAt = now;
                cur.UpdatedAt = now;
            }
            else
            {
                d.LastSyncedAt = now;
                ctx.HoldedExpenseDocs.Add(d);
            }
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedExpenseDoc>> GetUnmatchedAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedExpenseDocs.AsNoTracking()
            .Where(d => d.MatchStatus == HoldedMatchStatus.Unmatched)
            // arch:db-sort-ok newest-first — unmatched review list, most recent docs surface first
            .OrderByDescending(d => d.Date)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedExpenseDoc>> GetMatchedForYearAsync(int calendarYear, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedExpenseDocs.AsNoTracking()
            .Where(d => d.MatchStatus == HoldedMatchStatus.Matched && d.Date.Year == calendarYear)
            .ToListAsync(ct);
    }

    // ── Daybook journal lines (single source of truth) ────────────────────────

    public async Task UpsertLedgerLinesAsync(
        IReadOnlyList<HoldedLedgerLine> rows, Instant now, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Match existing by the natural key (EntryNumber, Line). Load by the incoming entry numbers,
        // then key in memory — journal lines are immutable, so this re-fetch is purely defensive idempotency.
        var entryNums = rows.Select(r => r.EntryNumber).Distinct().ToList();
        var existing = (await ctx.HoldedLedgerLines
                .Where(l => entryNums.Contains(l.EntryNumber))
                .ToListAsync(ct))
            .ToDictionary(l => (l.EntryNumber, l.Line));
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

    public async Task<Instant?> GetLatestLedgerLineDateAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedLedgerLines.AsNoTracking()
            // arch:db-sort-ok newest-first to read the single latest line date for incremental sync
            .OrderByDescending(l => l.Date)
            .Select(l => (Instant?)l.Date)
            .FirstOrDefaultAsync(ct);
    }

    // ── Creditor contact bindings ─────────────────────────────────────────────

    public async Task<HoldedCreditorContact?> GetCreditorContactByUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedCreditorContacts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId, ct);
    }

    public async Task<IReadOnlyList<HoldedCreditorContact>> GetCreditorContactsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedCreditorContacts.AsNoTracking().ToListAsync(ct);
    }

    public async Task UpsertCreditorContactAsync(
        HoldedCreditorContact row, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.HoldedCreditorContacts
            .FirstOrDefaultAsync(c => c.UserId == row.UserId, ct);
        if (existing is not null)
        {
            existing.HoldedContactId = row.HoldedContactId;
            if (row.SupplierAccountNum is not null) existing.SupplierAccountNum = row.SupplierAccountNum;
            existing.Source = row.Source;
            existing.UpdatedAt = now;
        }
        else
        {
            row.UpdatedAt = now;
            ctx.HoldedCreditorContacts.Add(row);
        }
        await ctx.SaveChangesAsync(ct);
    }

    // ── Sync state (singleton, seeded by migration) ──────────────────────────

    public async Task<HoldedSyncState> GetSyncStateAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedSyncStates.AsNoTracking().FirstAsync(s => s.Id == 1, ct);
    }

    public async Task SaveSyncStateAsync(HoldedSyncState state, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.HoldedSyncStates.FirstAsync(s => s.Id == 1, ct);
        ctx.Entry(existing).CurrentValues.SetValues(state);
        await ctx.SaveChangesAsync(ct);
    }
}

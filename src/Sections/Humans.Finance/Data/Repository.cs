using Humans.Finance.Domain;
using Humans.Finance.Models;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Finance.Data;

internal sealed class Repository(IDbContextFactory<FinanceDbContext> factory)
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
            .Where(d => ids.Contains(d.HoldedDocId)).ToDictionaryAsync(d => d.HoldedDocId, StringComparer.Ordinal, ct);
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
                cur.IsApproved = d.IsApproved;
                cur.TagsJson = d.TagsJson;
                cur.BookedAccountId = d.BookedAccountId;
                cur.BudgetCategoryId = d.BudgetCategoryId;
                cur.MatchStatus = d.MatchStatus;
                cur.MatchSource = d.MatchSource;
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

    public async Task<IReadOnlyList<HoldedExpenseDoc>> GetAllDocsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedExpenseDocs.AsNoTracking().ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedExpenseDoc>> GetMatchedForYearAsync(int calendarYear, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedExpenseDocs.AsNoTracking()
            .Where(d => d.MatchStatus == HoldedMatchStatus.Matched && d.Date.Year == calendarYear)
            .ToListAsync(ct);
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

    public async Task<bool> DeleteCreditorContactAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.HoldedCreditorContacts
            .FirstOrDefaultAsync(c => c.UserId == userId, ct);
        if (existing is null) return false;

        ctx.HoldedCreditorContacts.Remove(existing);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    // ── SEPA payouts ──────────────────────────────────────────────────────────

    public async Task AddSepaPayoutAsync(
        SepaPayoutFile file, IReadOnlyList<SepaPayoutTransfer> transfers, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.SepaPayoutFiles.Add(file);
        ctx.SepaPayoutTransfers.AddRange(transfers);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SepaPayoutExportRow>> GetSepaPayoutsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await (from t in ctx.SepaPayoutTransfers.AsNoTracking()
                      join f in ctx.SepaPayoutFiles on t.FileId equals f.Id
                      where t.UserId == userId
                      orderby f.GeneratedAt
                      select new SepaPayoutExportRow(
                          f.GeneratedAt, f.FileName, t.SupplierAccountNum,
                          t.CreditorName, t.IbanMasked, t.Amount))
            .ToListAsync(ct);
    }

    // ── Purchase-doc sync state (singleton, lazy-created) ─────────────────────

    public async Task<HoldedDocSyncState> GetOrCreateDocSyncStateAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.HoldedDocSyncStates.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (existing is not null) return existing;

        var created = new HoldedDocSyncState();
        ctx.HoldedDocSyncStates.Add(created);
        try
        {
            await ctx.SaveChangesAsync(ct);
            return created;
        }
        catch (DbUpdateException)
        {
            // Lost the Id=1 insert race to a concurrent caller; the winner's row is the singleton.
            return await ctx.HoldedDocSyncStates.AsNoTracking().FirstAsync(s => s.Id == 1, ct);
        }
    }

    public async Task SaveDocSyncStateAsync(HoldedDocSyncState state, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.HoldedDocSyncStates.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (existing is null)
        {
            ctx.HoldedDocSyncStates.Add(state);
        }
        else
        {
            ctx.Entry(existing).CurrentValues.SetValues(state);
        }
        await ctx.SaveChangesAsync(ct);
    }
}

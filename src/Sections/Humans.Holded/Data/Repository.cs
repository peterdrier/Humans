using Humans.Holded.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Holded.Data;

internal sealed class Repository(IDbContextFactory<HoldedDbContext> factory)
    : IHoldedMirrorRepository
{
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
                cur.EntryId = r.EntryId;
                cur.Checked = r.Checked;
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
        int accountNum, string? type = null, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var q = ctx.HoldedLedgerLines.AsNoTracking().Where(l => l.AccountNum == accountNum);
        if (type is not null) q = q.Where(l => l.Type == type);
        return await q.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedLedgerLine>> GetLedgerLinesByEntryNumbersAsync(
        IReadOnlyCollection<int> entryNumbers, CancellationToken ct = default)
    {
        if (entryNumbers.Count == 0) return [];
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var numbers = entryNumbers.Distinct().ToList();
        return await ctx.HoldedLedgerLines.AsNoTracking()
            .Where(l => numbers.Contains(l.EntryNumber))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedAccountTotals>> GetAccountTotalsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedLedgerLines.AsNoTracking()
            .GroupBy(l => l.AccountNum)
            .Select(g => new HoldedAccountTotals(g.Key, g.Sum(l => l.Debit), g.Sum(l => l.Credit), g.Count()))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedAccountTypeTotals>> GetAccountTypeTotalsAsync(
        int fromAccount, int toAccount, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedLedgerLines.AsNoTracking()
            .Where(l => l.AccountNum >= fromAccount && l.AccountNum <= toAccount)
            .GroupBy(l => new { l.AccountNum, l.Type })
            .Select(g => new HoldedAccountTypeTotals(
                g.Key.AccountNum, g.Key.Type, g.Sum(l => l.Debit), g.Sum(l => l.Credit)))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<int, decimal>> GetSumsByAccountAsync(
        IReadOnlyCollection<int> accounts, Instant from, Instant toExclusive, CancellationToken ct = default)
    {
        if (accounts.Count == 0) return new Dictionary<int, decimal>();
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var numbers = accounts.Distinct().ToList();
        return await ctx.HoldedLedgerLines.AsNoTracking()
            .Where(l => numbers.Contains(l.AccountNum) && l.Date >= from && l.Date < toExclusive)
            .GroupBy(l => l.AccountNum)
            .Select(g => new { g.Key, Sum = g.Sum(l => l.Debit) - g.Sum(l => l.Credit) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
    }

    public async Task<(int Lines, int Entries)> GetMirrorCountsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var lines = await ctx.HoldedLedgerLines.CountAsync(ct);
        var entries = await ctx.HoldedLedgerLines.Select(l => l.EntryNumber).Distinct().CountAsync(ct);
        return (lines, entries);
    }

    public async Task<bool> HasAnyLedgerLinesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedLedgerLines.AsNoTracking().AnyAsync(ct);
    }

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

    public async Task SetPurchaseDocNotesAsync(
        IReadOnlyDictionary<string, string> notesById, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ids = notesById.Keys.ToList();
        foreach (var doc in await ctx.HoldedPurchaseDocs.Where(d => ids.Contains(d.Id)).ToListAsync(ct))
            doc.Notes = notesById[doc.Id];
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ReplacePurchaseDocsAsync(
        IReadOnlyList<HoldedPurchaseDoc> docs, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ids = docs.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var existing = await ctx.HoldedPurchaseDocs.ToDictionaryAsync(d => d.Id, StringComparer.Ordinal, ct);
        ctx.HoldedPurchaseDocs.RemoveRange(existing.Values.Where(d => !ids.Contains(d.Id)));
        foreach (var d in docs)
        {
            if (existing.TryGetValue(d.Id, out var cur))
            {
                cur.DocNumber = d.DocNumber;
                cur.ContactName = d.ContactName;
                cur.ContactId = d.ContactId;
                cur.Description = d.Description;
                cur.Date = d.Date;
                cur.Subtotal = d.Subtotal;
                cur.Tax = d.Tax;
                cur.Total = d.Total;
                cur.Currency = d.Currency;
                cur.IsApproved = d.IsApproved;
                cur.PaymentsPending = d.PaymentsPending;
                cur.Notes = d.Notes ?? cur.Notes;
                cur.Tags = d.Tags;
                cur.BookedAccountId = d.BookedAccountId;
                cur.LastSyncedAt = now;
            }
            else
            {
                d.LastSyncedAt = now;
                ctx.HoldedPurchaseDocs.Add(d);
            }
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedPurchaseDoc>> GetPurchaseDocsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedPurchaseDocs.AsNoTracking().ToListAsync(ct);
    }

    public async Task ReplaceContactsAsync(
        IReadOnlyList<HoldedContact> contacts, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ids = contacts.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var existing = await ctx.HoldedContacts.ToDictionaryAsync(c => c.Id, StringComparer.Ordinal, ct);
        ctx.HoldedContacts.RemoveRange(existing.Values.Where(c => !ids.Contains(c.Id)));
        foreach (var c in contacts)
        {
            if (existing.TryGetValue(c.Id, out var cur))
            {
                cur.Name = c.Name;
                cur.SupplierAccountNum = c.SupplierAccountNum;
                cur.TradeName = c.TradeName;
                cur.Email = c.Email;
                cur.Phone = c.Phone;
                cur.Mobile = c.Mobile;
                cur.Iban = c.Iban;
                cur.TaxCode = c.TaxCode;
                cur.Address = c.Address;
                cur.LastSyncedAt = now;
            }
            else
            {
                c.LastSyncedAt = now;
                ctx.HoldedContacts.Add(c);
            }
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ReplaceBankMovementsAsync(
        string treasuryAccountId, LocalDate from, LocalDate to, IReadOnlyList<HoldedBankMovement> movements,
        Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ids = movements.Select(m => m.Id).ToList();
        // A line Holded re-dated across the window's edge is matched by id too.
        var existing = await ctx.HoldedBankMovements
            .Where(m => m.TreasuryAccountId == treasuryAccountId
                        && ((m.Date >= from && m.Date <= to) || ids.Contains(m.Id)))
            .ToDictionaryAsync(m => m.Id, StringComparer.Ordinal, ct);
        var keep = ids.ToHashSet(StringComparer.Ordinal);
        ctx.HoldedBankMovements.RemoveRange(existing.Values.Where(m => !keep.Contains(m.Id)));
        foreach (var m in movements)
        {
            if (existing.TryGetValue(m.Id, out var cur))
            {
                cur.Date = m.Date;
                cur.Amount = m.Amount;
                cur.Description = m.Description;
                cur.Status = m.Status;
                cur.LastSyncedAt = now;
            }
            else
            {
                m.LastSyncedAt = now;
                ctx.HoldedBankMovements.Add(m);
            }
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedBankMovement>> GetBankMovementsAsync(
        string treasuryAccountId, LocalDate from, LocalDate to, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedBankMovements.AsNoTracking()
            .Where(m => m.TreasuryAccountId == treasuryAccountId && m.Date >= from && m.Date <= to)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<HoldedContact>> GetContactsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedContacts.AsNoTracking().ToListAsync(ct);
    }

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

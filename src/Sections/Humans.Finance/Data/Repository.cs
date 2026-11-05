using Humans.Finance.Contracts;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Finance.Data;

internal sealed class Repository(IDbContextFactory<FinanceDbContext> factory)
    : IHoldedRepository
{
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

    public async Task<IReadOnlyList<string>> GetPleoBookingHoldsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.PleoBookingHolds.AsNoTracking().Select(h => h.PleoEntryId).ToListAsync(ct);
    }

    public async Task<bool> TryAddPleoBookingHoldAsync(string pleoEntryId, Instant heldAt)
    {
        await using var ctx = await factory.CreateDbContextAsync(CancellationToken.None);
        ctx.PleoBookingHolds.Add(new PleoBookingHold { PleoEntryId = pleoEntryId, HeldAt = heldAt });
        try
        {
            await ctx.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation
        })
        {
            return false;
        }
        return true;
    }

    public async Task<IReadOnlyList<string>> RemovePleoBookingHoldsAsync(
        IReadOnlyCollection<string>? pleoEntryIds, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = pleoEntryIds is null
            ? await ctx.PleoBookingHolds.ToListAsync(ct)
            : await ctx.PleoBookingHolds.Where(h => pleoEntryIds.Contains(h.PleoEntryId)).ToListAsync(ct);
        if (rows.Count == 0) return [];
        ctx.PleoBookingHolds.RemoveRange(rows);
        await ctx.SaveChangesAsync(ct);
        return rows.Select(h => h.PleoEntryId).ToList();
    }

    public async Task<IReadOnlyList<HoldedManagedAccount>> GetManagedAccountsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.HoldedManagedAccounts.AsNoTracking().ToListAsync(ct);
    }

    public async Task UpsertManagedAccountAsync(HoldedManagedAccount row, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.HoldedManagedAccounts
            .SingleOrDefaultAsync(a => a.HoldedAccountNumber == row.HoldedAccountNumber, ct);
        if (existing is null)
        {
            ctx.HoldedManagedAccounts.Add(row);
        }
        else
        {
            existing.HoldedAccountId = row.HoldedAccountId;
            existing.Label = row.Label;
            existing.IsActive = row.IsActive;
            existing.UpdatedAt = row.UpdatedAt;
        }
        await ctx.SaveChangesAsync(ct);
    }

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

    public async Task<bool> UpsertCreditorContactAsync(
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
        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_holded_creditor_contacts_UserId"
        })
        {
            // Another run bound this member after the existence check above.
            return false;
        }
        return true;
    }

    public async Task<bool> TryInsertCreditorContactAsync(
        HoldedCreditorContact row, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.HoldedCreditorContacts.Add(row);
        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_holded_creditor_contacts_UserId"
        })
        {
            // The member is already bound; the existing binding is untouched.
            return false;
        }
        return true;
    }

    public async Task<HoldedCreditorContact?> DeleteCreditorContactAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.HoldedCreditorContacts
            .FirstOrDefaultAsync(c => c.UserId == userId, ct);
        if (existing is null) return null;

        ctx.HoldedCreditorContacts.Remove(existing);
        await ctx.SaveChangesAsync(ct);
        return existing;
    }

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
                          f.GeneratedAt, f.FileName, t.SupplierAccountNum, t.HoldedContactId,
                          t.CreditorName, t.IbanMasked, t.Amount, t.BookedAt,
                          t.HoldedBankMovementId, t.ReconciledAt, t.WithdrawnAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SepaPayoutTransferRow>> GetSepaPayoutTransferRowsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await (from t in ctx.SepaPayoutTransfers.AsNoTracking()
                      join f in ctx.SepaPayoutFiles on t.FileId equals f.Id
                      select new SepaPayoutTransferRow(
                          t.Id, f.Id, f.FileName, f.GeneratedAt, f.GeneratedByUserId,
                          t.UserId, t.SupplierAccountNum, t.HoldedContactId,
                          t.CreditorName, t.IbanMasked, t.Amount,
                          t.BookedAt, t.BookedByUserId, t.HoldedBankMovementId, t.ReconciledAt,
                          t.HoldedPaymentRefs, null, null, null, null, null, null, false,
                          t.WithdrawnAt, t.WithdrawnByUserId))
            .ToListAsync(ct);
    }

    public async Task<SepaPayoutTransfer?> GetSepaTransferAsync(
        Guid transferId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.SepaPayoutTransfers.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transferId, ct);
    }

    public async Task SaveSepaTransferBookingAsync(
        Guid transferId, Instant bookedAt, Guid? bookedByUserId,
        string bankMovementId, Instant? reconciledAt, string? holdedPaymentRefs,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.SepaPayoutTransfers.FirstOrDefaultAsync(t => t.Id == transferId, ct);
        if (existing is null) return;

        existing.BookedAt = bookedAt;
        existing.BookedByUserId = bookedByUserId;
        existing.HoldedBankMovementId = bankMovementId;
        existing.ReconciledAt = reconciledAt;
        existing.HoldedPaymentRefs = holdedPaymentRefs;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task SaveSepaTransferPaymentRefsAsync(
        Guid transferId, string holdedPaymentRefs, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.SepaPayoutTransfers.FirstOrDefaultAsync(t => t.Id == transferId, ct);
        if (existing is null) return;

        existing.HoldedPaymentRefs = holdedPaymentRefs;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task SaveSepaTransfersWithdrawnAsync(
        IReadOnlyCollection<Guid> transferIds, Instant withdrawnAt, Guid withdrawnByUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.SepaPayoutTransfers.Where(t => transferIds.Contains(t.Id)).ToListAsync(ct);
        foreach (var t in existing)
        {
            t.WithdrawnAt = withdrawnAt;
            t.WithdrawnByUserId = withdrawnByUserId;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task MarkSepaTransferReconciledAsync(
        Guid transferId, Instant reconciledAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.SepaPayoutTransfers.FirstOrDefaultAsync(t => t.Id == transferId, ct);
        if (existing is null) return;

        existing.ReconciledAt = reconciledAt;
        await ctx.SaveChangesAsync(ct);
    }
}

using Humans.Finance.Contracts;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Finance.Data;

internal sealed class Repository(IDbContextFactory<FinanceDbContext> factory)
    : IFinanceRepository
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

    public async Task<HoldedCreditorContact?> MoveCreditorContactAsync(
        Guid fromUserId, Guid toUserId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var source = await ctx.HoldedCreditorContacts.FirstOrDefaultAsync(c => c.UserId == fromUserId, ct);
        if (source is null || await ctx.HoldedCreditorContacts.AnyAsync(c => c.UserId == toUserId, ct))
            return null;

        source.UserId = toUserId;
        source.UpdatedAt = now;
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
            // The target was bound after the check above; its binding stands.
            return null;
        }
        return source;
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

    public async Task<IReadOnlyDictionary<int, string>> GetStripeLedgerLinksAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.StripeLedgerLinks.AsNoTracking().ToDictionaryAsync(l => l.LedgerAccountNum, l => l.StripeAccountId, ct);
    }

    public async Task ReplaceStripeLedgerLinksAsync(IReadOnlyDictionary<int, string> links, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.StripeLedgerLinks.ToListAsync(ct);
        ctx.StripeLedgerLinks.RemoveRange(rows.Where(r => !links.ContainsKey(r.LedgerAccountNum)));
        foreach (var (num, accountId) in links)
        {
            if (rows.FirstOrDefault(r => r.LedgerAccountNum == num) is { } row)
                row.StripeAccountId = accountId;
            else
                ctx.StripeLedgerLinks.Add(new StripeLedgerLink { LedgerAccountNum = num, StripeAccountId = accountId });
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<FinanceSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == FinanceSettings.SingletonId, ct)
            ?? new FinanceSettings();
    }

    public async Task SaveSettingsAsync(FinanceSettings settings, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        if (await ctx.Settings.FirstOrDefaultAsync(s => s.Id == FinanceSettings.SingletonId, ct) is { } row)
        {
            row.PleoHoldedContactId = settings.PleoHoldedContactId;
            row.PleoHoldedTreasuryId = settings.PleoHoldedTreasuryId;
        }
        else
        {
            ctx.Settings.Add(settings);
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlySet<string>> GetPleoBookingClaimsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return (await ctx.PleoBookingClaims.AsNoTracking().Select(c => c.PleoEntryId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<bool> TryClaimPleoBookingAsync(string pleoEntryId, Instant at, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.PleoBookingClaims.Add(new PleoBookingClaim { PleoEntryId = pleoEntryId, ClaimedAt = at });
        try
        {
            await ctx.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_finance_pleo_booking_claims"
        })
        {
            return false;
        }
    }

    public async Task ReleasePleoBookingClaimAsync(string pleoEntryId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        await ctx.PleoBookingClaims.Where(c => c.PleoEntryId == pleoEntryId).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<PleoReceiptUpload>> GetPleoReceiptUploadsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.PleoReceiptUploads.AsNoTracking().ToListAsync(ct);
    }

    public async Task AddPleoReceiptUploadAsync(PleoReceiptUpload row, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.PleoReceiptUploads.Add(row);
        await ctx.SaveChangesAsync(ct);
    }
}


using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Humans.Tickets.Domain;

namespace Humans.Tickets.Data;

internal sealed class TicketTransferRepository(IDbContextFactory<TicketsDbContext> factory) : ITicketTransferRepository
{
    public async Task<IReadOnlyList<TicketTransferRequest>> GetApprovedAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketTransferRequests
            .AsNoTracking()
            .Where(r => r.Status == TicketTransferStatus.Approved)
            .ToListAsync(ct);
    }

    public async Task<int> ErasePiiForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var transfers = await ctx.TicketTransferRequests
            .Where(r => r.SenderUserId == userId
                     || r.ReceiverUserId == userId
                     || r.DecidedByUserId == userId)
            .ToListAsync(ct);
        if (transfers.Count == 0) return 0;

        foreach (var transfer in transfers)
        {
            // These fields are init-only: write through the tracked entry.
            var entry = ctx.Entry(transfer);
            if (transfer.ReceiverUserId == userId)
            {
                entry.Property(nameof(TicketTransferRequest.ReceiverLegalName)).CurrentValue =
                    TicketPiiTombstone.Name;
                entry.Property(nameof(TicketTransferRequest.ReceiverEmail)).CurrentValue =
                    TicketPiiTombstone.EmailFor(userId);
            }
            if (transfer.SenderUserId == userId)
            {
                entry.Property(nameof(TicketTransferRequest.SenderReason)).CurrentValue = string.Empty;
            }
            if (transfer.DecidedByUserId == userId)
            {
                transfer.AdminNotes = null;
            }
        }

        return await ctx.SaveChangesAsync(ct);
    }

    public async Task ReassignUserAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        if (Transaction.Current is not null)
        {
            await ReassignUserCoreAsync(ctx, sourceUserId, targetUserId, ct);
            return;
        }

        // ExecuteUpdateAsync auto-commits: without a transaction a failure between the two updates half-merges.
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        await ReassignUserCoreAsync(ctx, sourceUserId, targetUserId, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task ReassignUserCoreAsync(
        TicketsDbContext ctx,
        Guid sourceUserId,
        Guid targetUserId,
        CancellationToken ct)
    {
        await ctx.TicketTransferRequests
            .Where(r => r.SenderUserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SenderUserId, targetUserId), ct);
        await ctx.TicketTransferRequests
            .Where(r => r.ReceiverUserId == sourceUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReceiverUserId, targetUserId), ct);
    }
}

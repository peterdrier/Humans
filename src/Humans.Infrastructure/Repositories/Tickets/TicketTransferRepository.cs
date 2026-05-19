using System.Transactions;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Repositories.Tickets;

/// <summary>
/// EF-backed implementation of <see cref="ITicketTransferRepository"/>. Uses
/// <see cref="IDbContextFactory{TContext}"/> to maintain singleton registration
/// while keeping <c>HumansDbContext</c> short-lived (design-rules §15b).
/// </summary>
internal sealed class TicketTransferRepository(IDbContextFactory<HumansDbContext> factory) : ITicketTransferRepository
{
    public async Task<TicketTransferRequest?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketTransferRequests
            .Include(r => r.OriginalTicketAttendee)
                .ThenInclude(a => a!.TicketOrder)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<IReadOnlyList<TicketTransferRequest>> GetBySenderAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketTransferRequests
            .Where(r => r.SenderUserId == userId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TicketTransferRequest>> GetByStatusAsync(TicketTransferStatus status, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketTransferRequests
            .Include(r => r.OriginalTicketAttendee)
                .ThenInclude(a => a!.TicketOrder)
            .Where(r => r.Status == status)
            .ToListAsync(ct);
    }

    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.TicketTransferRequests.CountAsync(r => r.Status == TicketTransferStatus.Pending, ct);
    }

    public async Task AddAsync(TicketTransferRequest request, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.TicketTransferRequests.Add(request);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(TicketTransferRequest request, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Attach as Unchanged then mark only the root as Modified — `Update()` would
        // mark the whole reachable graph (OriginalTicketAttendee + TicketOrder)
        // as Modified, emitting spurious UPDATE statements against three tables
        // and double-writing the attendee in the Approve+Succeeded path.
        ctx.TicketTransferRequests.Attach(request).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ReassignUserAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        if (Transaction.Current is not null)
        {
            await ReassignUserCoreAsync(ctx, sourceUserId, targetUserId, ct);
            return;
        }

        // ExecuteUpdateAsync auto-commits — without an explicit transaction, a failure
        // between the Sender and Receiver updates would leave a half-merged state where
        // one column is re-pointed and the other still references the deleted source user.
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        await ReassignUserCoreAsync(ctx, sourceUserId, targetUserId, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task ReassignUserCoreAsync(
        HumansDbContext ctx,
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

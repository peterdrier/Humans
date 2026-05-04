using Humans.Application.Interfaces.Tickets;
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
public sealed class TicketTransferRepository : ITicketTransferRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public TicketTransferRepository(IDbContextFactory<HumansDbContext> factory) => _factory = factory;

    public async Task<TicketTransferRequest?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketTransferRequests
            .Include(r => r.OriginalTicketAttendee)
                .ThenInclude(a => a.TicketOrder)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<TicketTransferRequest?> GetPendingForAttendeeAsync(Guid attendeeId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketTransferRequests
            .FirstOrDefaultAsync(r => r.OriginalTicketAttendeeId == attendeeId &&
                                       r.Status == TicketTransferStatus.Pending, ct);
    }

    public async Task<IReadOnlyList<TicketTransferRequest>> GetByRequesterAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketTransferRequests
            .Where(r => r.RequesterUserId == userId)
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TicketTransferRequest>> GetByStatusAsync(TicketTransferStatus status, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketTransferRequests
            .Include(r => r.OriginalTicketAttendee)
                .ThenInclude(a => a.TicketOrder)
            .Where(r => r.Status == status)
            .OrderBy(r => r.RequestedAt)
            .ToListAsync(ct);
    }

    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.TicketTransferRequests.CountAsync(r => r.Status == TicketTransferStatus.Pending, ct);
    }

    public async Task AddAsync(TicketTransferRequest request, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.TicketTransferRequests.Add(request);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(TicketTransferRequest request, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.TicketTransferRequests.Update(request);
        await ctx.SaveChangesAsync(ct);
    }
}

using Humans.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Finance.Data;

internal sealed class PaymentDetailsRepository(IDbContextFactory<FinanceDbContext> factory)
    : IPaymentDetailsRepository
{
    public async Task<PaymentDetails?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.PaymentDetails.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
    }

    public async Task UpsertAsync(Guid userId, string? iban, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.PaymentDetails.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (row is null)
        {
            ctx.PaymentDetails.Add(new PaymentDetails { UserId = userId, Iban = iban, UpdatedAt = now });
        }
        else
        {
            row.Iban = iban;
            row.UpdatedAt = now;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.PaymentDetails.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (row is null) return false;
        ctx.PaymentDetails.Remove(row);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task ReassignAsync(Guid fromUserId, Guid toUserId, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var source = await ctx.PaymentDetails.SingleOrDefaultAsync(x => x.UserId == fromUserId, ct);
        if (source is null) return;

        // The key is the user id, so a move is a delete plus an insert in one save.
        ctx.PaymentDetails.Remove(source);
        if (!await ctx.PaymentDetails.AnyAsync(x => x.UserId == toUserId, ct))
            ctx.PaymentDetails.Add(new PaymentDetails { UserId = toUserId, Iban = source.Iban, UpdatedAt = now });
        await ctx.SaveChangesAsync(ct);
    }
}

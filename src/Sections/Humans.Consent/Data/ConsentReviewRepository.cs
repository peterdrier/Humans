using Humans.Consent.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Consent.Data;

internal sealed class ConsentReviewRepository(IDbContextFactory<LegalDbContext> factory)
    : IConsentReviewRepository
{
    public async Task<ConsentReview?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.ConsentReviews.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == userId, ct);
    }

    public async Task<IReadOnlyList<ConsentReview>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.ConsentReviews.AsNoTracking().ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ConsentReview>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.ConsentReviews.AsNoTracking().Where(r => userIds.Contains(r.UserId)).ToListAsync(ct);
    }

    public async Task UpsertAsync(ConsentReview values, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.ConsentReviews.SingleOrDefaultAsync(r => r.UserId == values.UserId, ct);
        if (row is null)
        {
            ctx.ConsentReviews.Add(Copy(values));
        }
        else
        {
            row.Status = values.Status;
            row.CheckedAt = values.CheckedAt;
            row.CheckedByUserId = values.CheckedByUserId;
            row.Notes = values.Notes;
            row.IsApproved = values.IsApproved;
            row.RejectionReason = values.RejectionReason;
            row.RejectedAt = values.RejectedAt;
            row.RejectedByUserId = values.RejectedByUserId;
            row.UpdatedAt = values.UpdatedAt;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> TryInsertAsync(ConsentReview values, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        if (await ctx.ConsentReviews.AnyAsync(r => r.UserId == values.UserId, ct))
            return false;
        ctx.ConsentReviews.Add(Copy(values));
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task EraseFreeTextAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.ConsentReviews.SingleOrDefaultAsync(r => r.UserId == userId, ct);
        if (row is null)
            return;
        row.Notes = null;
        row.RejectionReason = null;
        await ctx.SaveChangesAsync(ct);
    }

    private static ConsentReview Copy(ConsentReview v) => new()
    {
        UserId = v.UserId,
        Status = v.Status,
        CheckedAt = v.CheckedAt,
        CheckedByUserId = v.CheckedByUserId,
        Notes = v.Notes,
        IsApproved = v.IsApproved,
        RejectionReason = v.RejectionReason,
        RejectedAt = v.RejectedAt,
        RejectedByUserId = v.RejectedByUserId,
        UpdatedAt = v.UpdatedAt,
    };
}

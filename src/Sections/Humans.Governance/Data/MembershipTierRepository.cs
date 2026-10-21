using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Governance.Data;

internal sealed class MembershipTierRepository(IDbContextFactory<GovernanceDbContext> factory)
    : IMembershipTierRepository
{
    public async Task<MembershipTierRecord?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.MembershipTiers.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
    }

    public async Task<IReadOnlyList<MembershipTierRecord>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.MembershipTiers.AsNoTracking().ToListAsync(ct);
    }

    public async Task UpsertAsync(MembershipTierRecord values, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.MembershipTiers.SingleOrDefaultAsync(x => x.UserId == values.UserId, ct);
        if (row is null)
        {
            ctx.MembershipTiers.Add(Copy(values, values.UserId));
        }
        else
        {
            row.Tier = values.Tier;
            row.GrantedAt = values.GrantedAt;
            row.ExpiresAt = values.ExpiresAt;
            row.GrantedByApplicationId = values.GrantedByApplicationId;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> TryInsertAsync(MembershipTierRecord values, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        if (await ctx.MembershipTiers.AnyAsync(x => x.UserId == values.UserId, ct)) return false;
        ctx.MembershipTiers.Add(Copy(values, values.UserId));
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task UpdateExpiryForApplicationAsync(Guid applicationId, LocalDate? expiresAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.MembershipTiers.Where(x => x.GrantedByApplicationId == applicationId).ToListAsync(ct);
        if (rows.Count == 0) return;
        foreach (var row in rows) row.ExpiresAt = expiresAt;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ReassignAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var source = await ctx.MembershipTiers.SingleOrDefaultAsync(x => x.UserId == fromUserId, ct);
        if (source is null) return;

        // The key is the user id, so a move is a delete plus an insert in one save.
        ctx.MembershipTiers.Remove(source);
        if (!await ctx.MembershipTiers.AnyAsync(x => x.UserId == toUserId, ct))
            ctx.MembershipTiers.Add(Copy(source, toUserId));
        await ctx.SaveChangesAsync(ct);
    }

    private static MembershipTierRecord Copy(MembershipTierRecord v, Guid userId) => new()
    {
        UserId = userId,
        Tier = v.Tier,
        GrantedAt = v.GrantedAt,
        ExpiresAt = v.ExpiresAt,
        GrantedByApplicationId = v.GrantedByApplicationId,
    };
}

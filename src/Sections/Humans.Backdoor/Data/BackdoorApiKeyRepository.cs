using Humans.Backdoor.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Backdoor.Data;

internal sealed class BackdoorApiKeyRepository(IDbContextFactory<BackdoorDbContext> factory)
    : IBackdoorApiKeyRepository
{
    public async Task<BackdoorApiKey?> FindActiveByHashAsync(string keyHash, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.ApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash && k.RevokedAt == null, ct);
    }

    public async Task<IReadOnlyList<BackdoorApiKey>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.ApiKeys
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<BackdoorApiKey?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct);
    }

    public async Task AddAsync(BackdoorApiKey key, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.ApiKeys.Add(key);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> RevokeAsync(Guid id, Guid revokedByUserId, Instant at, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var key = await ctx.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.RevokedAt == null, ct);
        if (key is null) return false;

        key.RevokedAt = at;
        key.RevokedByUserId = revokedByUserId;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task TouchAsync(Guid id, Instant at, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        await ctx.ApiKeys
            .Where(k => k.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, at), ct);
    }

    public async Task<IReadOnlyList<BackdoorApiKey>> GetForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.ApiKeys
            .AsNoTracking()
            .Where(k => k.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        await ctx.ApiKeys.Where(k => k.UserId == userId).ExecuteDeleteAsync(ct);

        // Someone else's key they happened to issue or revoke: drop the back-reference,
        // keep the row. The key belongs to its owner, not to the admin who handled it.
        await ctx.ApiKeys
            .Where(k => k.CreatedByUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.CreatedByUserId, (Guid?)null), ct);

        await ctx.ApiKeys
            .Where(k => k.RevokedByUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedByUserId, (Guid?)null), ct);
    }

    public async Task ReassignToUserAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        await ctx.ApiKeys
            .Where(k => k.UserId == fromUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.UserId, toUserId), ct);

        await ctx.ApiKeys
            .Where(k => k.CreatedByUserId == fromUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.CreatedByUserId, (Guid?)toUserId), ct);

        await ctx.ApiKeys
            .Where(k => k.RevokedByUserId == fromUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedByUserId, (Guid?)toUserId), ct);
    }
}

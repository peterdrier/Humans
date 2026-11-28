using Humans.Backdoor.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Base.Data;

namespace Humans.Backdoor.Data;

internal sealed class BackdoorApiKeyRepository(IDbContextFactory<BackdoorDbContext> factory)
    : SectionRepository<BackdoorDbContext>(factory), IBackdoorApiKeyRepository
{
    public async Task<BackdoorApiKey?> FindActiveByHashAsync(string keyHash, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.ApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash && k.RevokedAt == null, ct), ct);

    public async Task<IReadOnlyList<BackdoorApiKey>> GetAllAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.ApiKeys
            .AsNoTracking()
            .ToListAsync(ct), ct);

    public async Task<BackdoorApiKey?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct), ct);

    public async Task AddAsync(BackdoorApiKey key, CancellationToken ct = default) =>
        await InsertAsync(key, ct);

    public async Task<bool> RevokeAsync(Guid id, Guid revokedByUserId, Instant at, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var key = await ctx.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.RevokedAt == null, ct);
        if (key is null) return false;

        key.RevokedAt = at;
        key.RevokedByUserId = revokedByUserId;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RotateAsync(
        Guid id, Guid revokedByUserId, Instant at, BackdoorApiKey replacement, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var key = await ctx.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.RevokedAt == null, ct);
        if (key is null) return false;

        key.RevokedAt = at;
        key.RevokedByUserId = revokedByUserId;
        ctx.ApiKeys.Add(replacement);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task TouchAsync(Guid id, Instant at, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        await ctx.ApiKeys
            .Where(k => k.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, at), ct);
    }

    public async Task<IReadOnlyList<BackdoorApiKey>> GetForUserAsync(Guid userId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.ApiKeys
            .AsNoTracking()
            .Where(k => k.UserId == userId)
            .ToListAsync(ct), ct);

    public async Task EraseForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);

        await ctx.ApiKeys.Where(k => k.UserId == userId).ExecuteDeleteAsync(ct);

        // Someone else's key they happened to create or revoke: drop the back-reference,
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
        await using var ctx = await CreateDbContextAsync(ct);

        await ctx.ApiKeys
            .Where(k => k.UserId == fromUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.UserId, toUserId), ct);

        await ctx.ApiKeys
            .Where(k => k.CreatedByUserId == fromUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.CreatedByUserId, toUserId), ct);

        await ctx.ApiKeys
            .Where(k => k.RevokedByUserId == fromUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedByUserId, toUserId), ct);
    }
}

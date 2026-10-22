using Microsoft.EntityFrameworkCore;

namespace Humans.Users.Data.Repositories;

internal sealed class CommunicationPreferenceRepository(IDbContextFactory<UsersDbContext> factory)
    : ICommunicationPreferenceRepository
{
    public async Task<int> DeleteAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.CommunicationPreferences
            .Where(cp => cp.UserId == userId)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        ctx.CommunicationPreferences.RemoveRange(rows);
        await ctx.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<IReadOnlyList<CommunicationPreference>> GetByUserIdReadOnlyAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CommunicationPreferences
            .AsNoTracking()
            .Where(cp => cp.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CommunicationPreference>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CommunicationPreferences
            .AsNoTracking()
            .ToListAsync(ct);
    }
}

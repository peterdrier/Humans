using Humans.Email.Contracts;
using Humans.Email.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Email.Data;

/// <summary>
/// EF-backed <see cref="ICommunicationPreferenceRepository"/>. Singleton over
/// <see cref="IDbContextFactory{TContext}"/>, like <see cref="EmailOutboxRepository"/>.
/// </summary>
internal sealed class CommunicationPreferenceRepository(IDbContextFactory<EmailDbContext> factory)
    : ICommunicationPreferenceRepository
{
    public async Task<IReadOnlyList<CommunicationPreference>> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CommunicationPreferences.AsNoTracking().Where(cp => cp.UserId == userId).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CommunicationPreference>> GetByCategoryAsync(
        MessageCategory category, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CommunicationPreferences.AsNoTracking().Where(cp => cp.Category == category).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CommunicationPreference>> GetForUsersAsync(
        IReadOnlyList<Guid> userIds, MessageCategory category, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CommunicationPreferences.AsNoTracking()
            .Where(cp => userIds.Contains(cp.UserId) && cp.Category == category)
            .ToListAsync(ct);
    }

    public async Task UpsertAsync(CommunicationPreference row, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.CommunicationPreferences
            .FirstOrDefaultAsync(cp => cp.UserId == row.UserId && cp.Category == row.Category, ct);
        if (existing is null)
            ctx.CommunicationPreferences.Add(row);
        else
            CopyState(row, existing);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid userId, MessageCategory category, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.CommunicationPreferences
            .FirstOrDefaultAsync(cp => cp.UserId == userId && cp.Category == category, ct);
        if (existing is null)
            return false;

        ctx.CommunicationPreferences.Remove(existing);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task ReplaceForUsersAsync(
        IReadOnlyList<(Guid UserId, IReadOnlyList<CommunicationPreference> Rows)> users, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        foreach (var (userId, rows) in users)
        {
            var existing = (await ctx.CommunicationPreferences.Where(cp => cp.UserId == userId).ToListAsync(ct))
                .ToDictionary(cp => cp.Category);
            foreach (var row in rows)
            {
                if (existing.Remove(row.Category, out var current))
                    CopyState(row, current);
                else
                    ctx.CommunicationPreferences.Add(row);
            }
            ctx.CommunicationPreferences.RemoveRange(existing.Values);
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.CommunicationPreferences.Where(cp => cp.UserId == userId).ToListAsync(ct);
        ctx.CommunicationPreferences.RemoveRange(rows);
        await ctx.SaveChangesAsync(ct);
        return rows.Count;
    }

    private static void CopyState(CommunicationPreference from, CommunicationPreference to)
    {
        to.OptedOut = from.OptedOut;
        to.InboxEnabled = from.InboxEnabled;
        to.UpdatedAt = from.UpdatedAt;
        to.UpdateSource = from.UpdateSource;
        to.SubscribedAt = from.SubscribedAt;
    }
}
